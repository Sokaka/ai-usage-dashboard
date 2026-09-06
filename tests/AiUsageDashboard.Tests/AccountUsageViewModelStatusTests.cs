using AiUsageDashboard.App;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class AccountUsageViewModelStatusTests
{
	[Fact]
	public void AccountMenus_WhenProfileStoreIsReadOnly_DoNotOfferSettingsMenu()
	{
		AccountUsageViewModel claude = new(
			CreateProfile(ProviderKind.Claude),
			canManage: false);
		AccountUsageViewModel copilot = new(
			CreateProfile(ProviderKind.Copilot),
			canManage: false);
		AccountUsageViewModel disabledClaude = new(
			CreateProfile(ProviderKind.Claude) with { IsEnabled = false },
			canManage: false);

		Assert.False(claude.CanChangeAccountSettings);
		Assert.False(claude.CanOpenMainAccountMenu);
		Assert.False(claude.CanOpenWidgetAccountMenu);
		Assert.False(copilot.CanOpenMainAccountMenu);
		Assert.True(copilot.CanOpenWidgetAccountMenu);
		Assert.False(disabledClaude.CanOpenMainAccountMenu);
		Assert.False(disabledClaude.CanOpenWidgetAccountMenu);
		Assert.True(disabledClaude.CanConnectProviderAccountAfterSave);
	}

	[Theory]
	[InlineData(ProviderKind.Claude, "Claude Code", "登入憑證")]
	[InlineData(ProviderKind.Codex, "Codex CLI", "登入憑證")]
	[InlineData(ProviderKind.Antigravity, "Antigravity", "登入資料")]
	public void AccountDeletionConfirmationText_ExplainsExactLocalScope(
		ProviderKind provider,
		string expectedProviderDetail,
		string expectedRetainedData)
	{
		AccountProfile profile = CreateProfile(provider) with
		{
			DisplayName = "工作帳號"
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.SeedProviderAccountIdentity("person@example.com");

		string message = viewModel.AccountDeletionConfirmationText;

		Assert.Contains("暱稱：工作帳號", message, StringComparison.Ordinal);
		Assert.Contains(
			$"服務：{viewModel.ProviderName}",
			message,
			StringComparison.Ordinal);
		Assert.Contains(
			"帳號：person@example.com",
			message,
			StringComparison.Ordinal);
		Assert.Contains(
			"嘗試清除上次用量",
			message,
			StringComparison.Ordinal);
		Assert.Contains(expectedProviderDetail, message, StringComparison.Ordinal);
		Assert.Contains(expectedRetainedData, message, StringComparison.Ordinal);

		if (provider == ProviderKind.Antigravity)
		{
			Assert.Contains(
				"AI Usage 也會移除自己建立的用量顯示設定",
				message,
				StringComparison.Ordinal);
			Assert.Contains(
				"其他 Antigravity 設定不受影響",
				message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"目前 Windows 使用者設定都會保留",
				message,
				StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData("", "Claude", "Claude", "", false)]
	[InlineData("工作", "工作", "Claude · 工作", " · 工作", true)]
	public void AccountTitle_OnlyShowsNicknameWhenProvided(
		string nickname,
		string expectedAccountName,
		string expectedTitle,
		string expectedHeaderSuffix,
		bool expectedHasNickname)
	{
		AccountUsageViewModel viewModel = new(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, nickname),
			canManage: true);

		Assert.Equal(nickname, viewModel.AccountNickname);
		Assert.Equal(expectedAccountName, viewModel.AccountName);
		Assert.Equal(expectedAccountName, viewModel.AccountCardTitle);
		Assert.Equal(expectedTitle, viewModel.AccountTitle);
		Assert.Equal(expectedTitle, viewModel.AccountHeaderText);
		Assert.Equal(expectedHeaderSuffix, viewModel.AccountHeaderSuffixText);
		Assert.Equal(expectedHasNickname, viewModel.HasAccountNickname);
		Assert.Equal(expectedHasNickname, viewModel.HasAccountAlias);
		Assert.Equal(
			expectedHasNickname ? $"暱稱 · {nickname}" : string.Empty,
			viewModel.AccountAliasText);

		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		viewModel.ApplyProfile(viewModel.Profile with { DisplayName = "更新後" }, canManage: true);

		Assert.Equal("Claude · 更新後", viewModel.AccountHeaderText);
		Assert.Equal(" · 更新後", viewModel.AccountHeaderSuffixText);
		Assert.Contains(nameof(AccountUsageViewModel.AccountHeaderText), changedProperties);
		Assert.Contains(nameof(AccountUsageViewModel.AccountHeaderSuffixText), changedProperties);
	}

	[Fact]
	public void AccountName_WithoutNickname_UsesIdentityForNonVisualContext()
	{
		AccountUsageViewModel viewModel = new(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, string.Empty),
			canManage: true);

		viewModel.SeedProviderAccountIdentity("person@example.com");

		Assert.Equal("Claude", viewModel.AccountCardTitle);
		Assert.Equal("Claude", viewModel.AccountTitle);
		Assert.Equal("Claude · person@example.com", viewModel.AccountName);
	}

	[Fact]
	public void GrokPresentation_MasksOpaqueBindingIdentity()
	{
		const string OpaqueBindingIdentity = "grok-public-binding-id";
		AccountProfile profile = CreateProfile(
			ProviderKind.Grok,
			providerAccountIdentity: OpaqueBindingIdentity) with
		{
			DisplayName = string.Empty
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric(
					"grok.weekly",
					"每週用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: OpaqueBindingIdentity));

		Assert.True(viewModel.IsGrok);
		Assert.False(viewModel.SupportsSubscriptionContext);
		Assert.False(viewModel.ShowSubscriptionContext);
		Assert.Empty(viewModel.SubscriptionContextMenuText);
		Assert.Equal("Grok", viewModel.ProviderName);
		Assert.Equal(
			"Grok · 已連接的 Grok 帳號",
			viewModel.AccountName);
		Assert.Equal("Grok", viewModel.AccountCardTitle);
		Assert.Equal("Grok", viewModel.AccountTitle);
		Assert.Equal(
			"已連接的 Grok 帳號",
			viewModel.AccountDisplayText);
		Assert.Equal(
			"已連接的 Grok 帳號",
			viewModel.ProviderAccountDisplayText);
		Assert.Contains(
			"帳號：已連接的 Grok 帳號",
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
		Assert.Contains(
			"另外儲存的 Grok Build CLI 登入資料",
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			OpaqueBindingIdentity,
			viewModel.AccountName,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			OpaqueBindingIdentity,
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			OpaqueBindingIdentity,
			viewModel.ProviderAccountDisplayText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			OpaqueBindingIdentity,
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
	}

	[Fact]
	public void GrokPresentation_WithMatchingReportedEmail_ShowsEmailAndNotifiesBindings()
	{
		const string OpaqueBindingIdentity = "grok-public-binding-id";
		const string AccountEmail = "person@example.com";
		AccountProfile profile = CreateProfile(
			ProviderKind.Grok,
			providerAccountIdentity: OpaqueBindingIdentity) with
		{
			DisplayName = string.Empty
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric(
					"grok.weekly",
					"每週用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: OpaqueBindingIdentity,
			ProviderAccountDisplayIdentity: AccountEmail));

		Assert.Equal($"Grok · {AccountEmail}", viewModel.AccountName);
		Assert.Equal(AccountEmail, viewModel.AccountDisplayText);
		Assert.Contains(
			$"帳號：{AccountEmail}",
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
		Assert.Contains(nameof(AccountUsageViewModel.AccountName), changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.AccountDisplayText),
			changedProperties);
		Assert.DoesNotContain(
			OpaqueBindingIdentity,
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
	}

	[Fact]
	public void GrokPresentation_WithMismatchedBinding_DoesNotShowReportedEmail()
	{
		const string AccountEmail = "wrong-account@example.com";
		AccountProfile profile = CreateProfile(
			ProviderKind.Grok,
			providerAccountIdentity: "expected-binding") with
		{
			DisplayName = string.Empty
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric(
					"grok.weekly",
					"每週用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: "different-binding",
			ProviderAccountDisplayIdentity: AccountEmail));

		Assert.DoesNotContain(
			AccountEmail,
			viewModel.AccountName,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			AccountEmail,
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
		Assert.Equal(SnapshotStatus.Error, viewModel.CurrentSnapshot?.Status);
	}

	[Fact]
	public void GrokPresentation_WhenUsageIsUnavailable_ShowsVerifiedEmail()
	{
		const string OpaqueBindingIdentity = "grok-public-binding-id";
		AccountProfile profile = CreateProfile(
			ProviderKind.Grok,
			providerAccountIdentity: OpaqueBindingIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "Grok account scope is verified, but usage is unavailable.",
			ProviderAccountIdentity: OpaqueBindingIdentity,
			RecoveryAction: UsageRecoveryAction.Retry,
			ProviderAccountDisplayIdentity: "person@example.com",
			SubscriptionVerificationState:
				SubscriptionVerificationState.UsageUnavailable));

		Assert.Equal("person@example.com", viewModel.AccountDisplayText);
		Assert.DoesNotContain(
			OpaqueBindingIdentity,
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
	}

	[Fact]
	public void GrokPresentation_WhenScopeProbeIsTransient_ShowsLastConfirmedEmail()
	{
		const string OpaqueBindingIdentity = "grok-public-binding-id";
		AccountProfile profile = CreateProfile(
			ProviderKind.Grok,
			providerAccountIdentity: OpaqueBindingIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow,
			Error: "Grok usage probe is temporarily unavailable.",
			ProviderAccountIdentity: OpaqueBindingIdentity,
			RecoveryAction: UsageRecoveryAction.Retry,
			ProviderAccountDisplayIdentity: "person@example.com",
			SubscriptionVerificationState:
				SubscriptionVerificationState.TransientProbeError));

		Assert.Equal(
			"person@example.com（上次確認）",
			viewModel.AccountDisplayText);
	}

	[Fact]
	public void ClaudePresentation_WithoutOrganizationName_UsesOpaqueContextFallbackAndPlan()
	{
		const string PublicBindingIdentity = "00112233445566778899aabbccddeeff";
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: PublicBindingIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: PublicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			PlanTier: "max",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified));

		Assert.Equal(
			"person@example.com · 範圍 00112233 · 方案 max",
			viewModel.AccountDisplayText);
		Assert.True(viewModel.SupportsSubscriptionContext);
		Assert.Equal("顯示組織", viewModel.SubscriptionContextMenuText);
		Assert.Equal(
			"person@example.com",
			viewModel.AccountCardDisplayText);
		Assert.Equal("max", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Claude · max · 測試帳號", viewModel.AccountHeaderText);
		Assert.Equal(" · max · 測試帳號", viewModel.AccountHeaderSuffixText);

		viewModel.ApplyProfile(
			profile with { ShowSubscriptionContext = true },
			canManage: true);

		Assert.Equal(
			$"person@example.com{Environment.NewLine}範圍 00112233",
			viewModel.AccountCardDisplayText);
	}

	[Fact]
	public void ClaudePresentation_WithSameEmailAndDifferentOpaqueBindings_RemainsDistinct()
	{
		const string FirstBindingIdentity = "00112233445566778899aabbccddeeff";
		const string SecondBindingIdentity = "ffeeddccbbaa99887766554433221100";
		AccountUsageViewModel first = CreateClaudeContextViewModel(
			FirstBindingIdentity,
			SubscriptionVerificationState.Verified);
		AccountUsageViewModel second = CreateClaudeContextViewModel(
			SecondBindingIdentity,
			SubscriptionVerificationState.Verified);

		Assert.NotEqual(first.AccountDisplayText, second.AccountDisplayText);
		Assert.Contains("範圍 00112233", first.AccountDisplayText);
		Assert.Contains("範圍 FFEEDDCC", second.AccountDisplayText);
	}

	[Fact]
	public void ClaudePresentation_WithTransientProbe_ShowsLastConfirmedContextMetadata()
	{
		const string PublicBindingIdentity = "00112233445566778899aabbccddeeff";
		AccountUsageViewModel viewModel = CreateClaudeContextViewModel(
			PublicBindingIdentity,
			SubscriptionVerificationState.TransientProbeError,
			"Company Team");

		Assert.Equal(
			"person@example.com · Company Team · 方案 max（上次確認）",
			viewModel.AccountDisplayText);
		Assert.Equal(
			"person@example.com（上次確認）",
			viewModel.AccountCardDisplayText);
		Assert.Equal("max", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Claude · max · 測試帳號", viewModel.AccountHeaderText);
		Assert.Equal(" · max · 測試帳號", viewModel.AccountHeaderSuffixText);

		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);
		viewModel.ApplyProfile(
			viewModel.Profile with { ShowSubscriptionContext = true },
			canManage: true);

		Assert.Equal(
			$"person@example.com{Environment.NewLine}Company Team（上次確認）",
			viewModel.AccountCardDisplayText);
		Assert.Contains(
			nameof(AccountUsageViewModel.AccountCardDisplayText),
			changedProperties);

		viewModel.ApplyProfile(
			viewModel.Profile with { ShowSubscriptionContext = false },
			canManage: true);

		Assert.Equal(
			"person@example.com（上次確認）",
			viewModel.AccountCardDisplayText);
	}

	[Fact]
	public void CodexPresentation_WithVerifiedSnapshot_ShowsWorkspaceOnlyWhenEnabled()
	{
		const string PublicBindingIdentity = "00112233445566778899aabbccddeeff";
		AccountProfile profile = CreateProfile(
			ProviderKind.Codex,
			providerAccountIdentity: PublicBindingIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: PublicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			PlanTier: "team",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified));

		Assert.Equal(
			"person@example.com · 進階 workspace · 本機連接碼 00112233 · 方案 team",
			viewModel.AccountDisplayText);
		Assert.True(viewModel.SupportsSubscriptionContext);
		Assert.Equal("顯示 workspace", viewModel.SubscriptionContextMenuText);
		Assert.Equal(
			"person@example.com",
			viewModel.AccountCardDisplayText);
		Assert.Equal("team", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Codex · team · 測試帳號", viewModel.AccountHeaderText);
		Assert.Equal(" · team · 測試帳號", viewModel.AccountHeaderSuffixText);

		viewModel.ApplyProfile(
			profile with { ShowSubscriptionContext = true },
			canManage: true);

		Assert.Equal(
			$"person@example.com{Environment.NewLine}進階 workspace · 本機連接碼 00112233",
			viewModel.AccountCardDisplayText);
		Assert.DoesNotContain(
			PublicBindingIdentity,
			viewModel.AccountDisplayText,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(SnapshotStatus.Ready)]
	[InlineData(SnapshotStatus.Stale)]
	public void CodexPresentation_WithStandardConnection_ShowsPlanBesideProvider(
		SnapshotStatus status)
	{
		const string AccountIdentity = "person@example.com";
		AccountProfile profile = CreateProfile(
			ProviderKind.Codex,
			providerAccountIdentity: AccountIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			[
				new UsageMetric(
					"codex.primary",
					"5 小時用量",
					25,
					"已使用 25%")
			],
			SourceTrust.OfficialExperimental,
			status,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: AccountIdentity,
			ProviderAccountDisplayIdentity: AccountIdentity,
			PlanTier: "pro",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Unverified));

		Assert.Equal("pro", viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Codex · pro · 測試帳號", viewModel.AccountHeaderText);
		Assert.Equal(" · pro · 測試帳號", viewModel.AccountHeaderSuffixText);
		Assert.Equal(
			status == SnapshotStatus.Stale
				? $"{AccountIdentity}（上次確認）"
				: AccountIdentity,
			viewModel.AccountCardDisplayText);
	}

	[Fact]
	public void CodexPresentation_WithUnverifiedWorkspace_DoesNotShowPlan()
	{
		const string PublicBindingIdentity =
			"00112233445566778899aabbccddeeff";
		AccountProfile profile = CreateProfile(
			ProviderKind.Codex,
			providerAccountIdentity: PublicBindingIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			[
				new UsageMetric(
					"codex.primary",
					"5 小時用量",
					25,
					"已使用 25%")
			],
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: PublicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			PlanTier: "team",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Unverified));

		Assert.Empty(viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Codex · 測試帳號", viewModel.AccountHeaderText);
		Assert.Equal(" · 測試帳號", viewModel.AccountHeaderSuffixText);
	}

	[Theory]
	[InlineData("other@example.com", SourceTrust.OfficialExperimental)]
	[InlineData("person@example.com", SourceTrust.Estimated)]
	public void CodexPresentation_WithUntrustedStandardPlan_DoesNotShowPlan(
		string displayIdentity,
		SourceTrust sourceTrust)
	{
		const string AccountIdentity = "person@example.com";
		AccountProfile profile = CreateProfile(
			ProviderKind.Codex,
			providerAccountIdentity: AccountIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			[
				new UsageMetric(
					"codex.primary",
					"5 小時用量",
					25,
					"已使用 25%")
			],
			sourceTrust,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: AccountIdentity,
			ProviderAccountDisplayIdentity: displayIdentity,
			PlanTier: "pro",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Unverified));

		Assert.Empty(viewModel.SubscriptionPlanDisplayText);
		Assert.Equal("Codex · 測試帳號", viewModel.AccountHeaderText);
		Assert.Equal(" · 測試帳號", viewModel.AccountHeaderSuffixText);
	}

	[Fact]
	public void CodexPresentation_WithoutSnapshot_MasksOpaqueBindingIdentity()
	{
		const string PublicBindingIdentity = "00112233445566778899aabbccddeeff";
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Codex,
				providerAccountIdentity: PublicBindingIdentity),
			canManage: true);

		Assert.Equal(
			"已連接的 Codex workspace（上次確認）",
			viewModel.AccountDisplayText);
		Assert.DoesNotContain(
			PublicBindingIdentity,
			viewModel.AccountName,
			StringComparison.Ordinal);
	}

	[Fact]
	public void ResetImportedMachineLocalConnectionState_ForCodex_ClearsPublicIdentityAndSnapshot()
	{
		const string PublicBindingIdentity = "00112233445566778899aabbccddeeff";
		AccountProfile profile = CreateProfile(
			ProviderKind.Codex,
			providerAccountIdentity: PublicBindingIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: PublicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			SubscriptionVerificationState:
				SubscriptionVerificationState.Verified));

		viewModel.ResetImportedMachineLocalConnectionState();

		Assert.Null(viewModel.Profile.ProviderAccountIdentity);
		Assert.Null(viewModel.ProviderAccountIdentity);
		Assert.Null(viewModel.CurrentSnapshot);
	}

	[Fact]
	public void GrokConnectionAndInstallRecovery_UseGrokBuildActions()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Grok);
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		Assert.True(viewModel.ShowGrokDefaultConnectionAction);
		Assert.True(viewModel.CanOpenMainAccountMenu);
		Assert.Equal("連接 Grok 帳號", viewModel.GrokAccountActionText);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Unsupported,
			DateTimeOffset.UtcNow,
			RecoveryAction: UsageRecoveryAction.InstallOrUpdate));

		Assert.False(viewModel.ShowGrokDefaultConnectionAction);
		Assert.Equal("需要安裝或更新", viewModel.RecoveryPanelTitle);
		Assert.Equal(
			"查看 Grok Build CLI 處理方式",
			viewModel.RecoveryActionText);
		Assert.Contains(
			"xAI 官方來源",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AccountDeletionConfirmationText_WithoutNickname_OmitsNicknameRow()
	{
		AccountUsageViewModel viewModel = new(
			new AccountProfile(Guid.NewGuid(), ProviderKind.Claude, string.Empty),
			canManage: true);

		Assert.DoesNotContain(
			"暱稱：",
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
		Assert.Contains(
			"服務：Claude",
			viewModel.AccountDeletionConfirmationText,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		SnapshotStatus.Ready,
		AccountStatusKind.Ready,
		AccountStatusSeverity.Positive,
		"可用")]
	[InlineData(
		SnapshotStatus.Refreshing,
		AccountStatusKind.Refreshing,
		AccountStatusSeverity.Informational,
		"檢查中")]
	[InlineData(
		SnapshotStatus.Stale,
		AccountStatusKind.Stale,
		AccountStatusSeverity.Warning,
		"顯示上次資料")]
	[InlineData(
		SnapshotStatus.Error,
		AccountStatusKind.Error,
		AccountStatusSeverity.Critical,
		"讀取失敗")]
	[InlineData(
		SnapshotStatus.NotConfigured,
		AccountStatusKind.NotConfigured,
		AccountStatusSeverity.Warning,
		"尚未設定")]
	[InlineData(
		SnapshotStatus.Unsupported,
		AccountStatusKind.Unsupported,
		AccountStatusSeverity.Neutral,
		"暫不支援")]
	public void ApplySnapshot_ProjectsStableSemanticStatus(
		SnapshotStatus snapshotStatus,
		AccountStatusKind expectedKind,
		AccountStatusSeverity expectedSeverity,
		string expectedText)
	{
		AccountProfile profile = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Official,
			snapshotStatus,
			DateTimeOffset.UtcNow);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(expectedKind, viewModel.StatusKind);
		Assert.Equal(expectedSeverity, viewModel.StatusSeverity);
		Assert.Equal(expectedText, viewModel.StatusText);
	}

	[Fact]
	public void ApplySnapshot_WhenSameSnapshotIsReapplied_DoesNotRebuildProjection()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: "person@example.com");
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"claude.five_hour",
					"5 小時用量",
					25,
					"已使用 25%")
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			new DateTimeOffset(2026, 7, 26, 1, 2, 3, TimeSpan.Zero),
			ProviderAccountIdentity: profile.ProviderAccountIdentity);
		viewModel.ApplySnapshot(snapshot);
		IReadOnlyList<UsageMetricViewModel> projectedMetrics = viewModel.UsageMetrics;
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArguments) =>
			changedProperties.Add(eventArguments.PropertyName);

		viewModel.ApplySnapshot(snapshot);

		Assert.Same(projectedMetrics, viewModel.UsageMetrics);
		Assert.Empty(changedProperties);
	}

	[Theory]
	[InlineData(
		UsageRecoveryAction.Retry,
		"稍後會自動再試",
		"",
		"這次未取得新用量，稍後會自動再試，不需要手動操作。",
		false)]
	[InlineData(
		UsageRecoveryAction.ConnectAccount,
		"尚未連接",
		"連接 Claude 帳號",
		"尚未連接 Claude 帳號。連接後即可讀取用量。",
		true)]
	[InlineData(
		UsageRecoveryAction.ConfirmSubscription,
		"需要確認 Claude 訂閱",
		"確認 Claude 訂閱",
		"請使用這張卡片原本的 Claude 帳號，確認組織與訂閱方案。完成後即可重新檢查用量。",
		true)]
	[InlineData(
		UsageRecoveryAction.SwitchAccount,
		"需要切換帳號",
		"切換 Claude 帳號",
		"目前的 Claude 帳號或登入方式無法讀取用量，請切換帳號。",
		true)]
	[InlineData(
		UsageRecoveryAction.ReconfigureUsageSource,
		"需要確認用量讀取",
		"開啟設定說明",
		"請依卡片提示重新確認用量讀取，再檢查一次。帳號不必重新登入。",
		true)]
	[InlineData(
		UsageRecoveryAction.RestartApplication,
		"需要重新啟動",
		"重新啟動 AI Usage",
		"用量檢查已暫停，以避免重複檢查。重新啟動 AI Usage 後才會再試。",
		true)]
	[InlineData(
		UsageRecoveryAction.InstallOrUpdate,
		"需要安裝或更新",
		"查看 Claude CLI 處理方式",
		"目前的 Claude CLI 無法使用。安裝、更新或修復後不必重新連接帳號。",
		true)]
	[InlineData(
		UsageRecoveryAction.UpdateApplication,
		"需要更新 AI Usage",
		"查看 AI Usage 更新方式",
		"目前的 AI Usage 尚未支援這個用量格式。更新後會自動重新檢查；不必重新連接帳號。",
		true)]
	[InlineData(
		UsageRecoveryAction.RevalidateUsage,
		"需要重新檢查",
		"重新檢查 Claude 用量",
		"上次 Claude 用量檢查未完成。按下後只會重新檢查這個帳號一次，不會重新啟動 AI Usage 或登入 Claude。",
		true)]
	public void ApplySnapshot_ProjectsRecoveryAction(
		UsageRecoveryAction recoveryAction,
		string expectedPanelTitle,
		string expectedActionText,
		string expectedDescription,
		bool expectedExecutable)
	{
		AccountProfile profile = CreateProfile(ProviderKind.Claude) with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Official,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			RecoveryAction: recoveryAction);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(recoveryAction, viewModel.RecoveryAction);
		Assert.True(viewModel.HasRecoveryAction);
		Assert.True(viewModel.ShowRecoveryActionNotice);
		Assert.Equal(expectedPanelTitle, viewModel.RecoveryPanelTitle);
		Assert.Equal(expectedExecutable, viewModel.HasExecutableRecoveryAction);
		Assert.Equal(expectedExecutable, viewModel.CanExecuteRecoveryAction);
		Assert.Equal(expectedActionText, viewModel.RecoveryActionText);
		Assert.Equal(expectedDescription, viewModel.RecoveryActionDescription);
	}

	[Fact]
	public void ApplySnapshot_WithUnsupportedAntigravityShape_PointsToAiUsageUpdate()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Antigravity,
			providerAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "目前 AI Usage 尚未支援這個 Antigravity 用量格式。",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			RecoveryAction: UsageRecoveryAction.UpdateApplication));

		Assert.Equal("需要更新 AI Usage", viewModel.RecoveryPanelTitle);
		Assert.Equal("查看 AI Usage 更新方式", viewModel.RecoveryActionText);
		Assert.Equal(
			"目前的 AI Usage 尚未支援這個 Antigravity 用量格式。更新 AI Usage 後會自動重新檢查。既有 Antigravity 連接通常可沿用；若卡片後續要求，請重新確認連接。",
			viewModel.RecoveryActionDescription);
		Assert.DoesNotContain(
			"Antigravity CLI",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
	}

	[Fact]
	public void ApplySnapshot_WithUnavailableAntigravityCli_PointsToAntigravityHandling()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Antigravity,
			providerAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "找不到支援的 Antigravity CLI。",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			RecoveryAction: UsageRecoveryAction.InstallOrUpdate));

		Assert.Equal("需要安裝或更新", viewModel.RecoveryPanelTitle);
		Assert.Equal("查看 Antigravity 處理方式", viewModel.RecoveryActionText);
		Assert.Equal(
			"找不到可用的 Antigravity CLI，或目前的 Antigravity 版本或 AI Usage 尚未支援用量檢查。請確認 Antigravity 已安裝並更新 Antigravity 或 AI Usage。既有 Antigravity 連接通常可沿用；若卡片後續要求，請重新確認連接。",
			viewModel.RecoveryActionDescription);
	}

	[Fact]
	public void UsageSafetyRevalidationInProgress_DisablesRecoveryAction()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Antigravity,
			providerAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			RecoveryAction: UsageRecoveryAction.RevalidateUsage));

		Assert.True(viewModel.CanExecuteRecoveryAction);
		Assert.True(viewModel.CanInvokeRecoveryAction);
		Assert.True(viewModel.CanRefreshUsage);

		viewModel.BeginUsageSafetyRevalidation(1);

		Assert.True(viewModel.IsUsageSafetyRevalidationInProgress);
		Assert.False(viewModel.CanExecuteRecoveryAction);
		Assert.False(viewModel.CanInvokeRecoveryAction);
		Assert.False(viewModel.CanRefreshUsage);

		viewModel.BeginUsageSafetyRevalidation(2);
		viewModel.CompleteUsageSafetyRevalidation(1);

		Assert.True(viewModel.IsUsageSafetyRevalidationInProgress);
		Assert.False(viewModel.CanRefreshUsage);

		viewModel.CompleteUsageSafetyRevalidation(2);

		Assert.False(viewModel.IsUsageSafetyRevalidationInProgress);
		Assert.True(viewModel.CanExecuteRecoveryAction);
		Assert.True(viewModel.CanInvokeRecoveryAction);
		Assert.True(viewModel.CanRefreshUsage);
	}

	[Fact]
	public void ApplySnapshot_WhenAntigravityFiveHourDataExpired_ShowsWaitingRowsUntilRecovery()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountProfile profile = CreateProfile(
			ProviderKind.Antigravity,
			providerAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageMetric[] weeklyMetrics =
		[
			new(
				"agy.gemini.weekly",
				"Gemini weekly",
				20,
				"已使用 20%",
				now.AddDays(3)),
			new(
				"agy.claude.weekly",
				"Claude weekly",
				30,
				"已使用 30%",
				now.AddDays(4))
		];
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			weeklyMetrics,
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			now,
			Error: "上次用量檢查未完成。",
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity,
			RecoveryAction: UsageRecoveryAction.RevalidateUsage));

		Assert.Equal(
			new[]
			{
				"agy.gemini.rolling-5h",
				"agy.gemini.weekly",
				"agy.claude.rolling-5h",
				"agy.claude.weekly"
			},
			viewModel.UsageMetrics.Select(metric => metric.Key));
		UsageMetricViewModel[] waitingMetrics = viewModel.UsageMetrics
			.Where(metric => metric.Key.EndsWith(
				"rolling-5h",
				StringComparison.Ordinal))
			.ToArray();
		Assert.Equal(
			new[] { "5小時用量 · Gemini", "5小時用量 · Claude + GPT" },
			waitingMetrics.Select(metric => metric.Label));
		Assert.All(waitingMetrics, metric =>
		{
			Assert.Equal("等待重新檢查", metric.DisplayValue);
			Assert.False(metric.HasUsageBar);
			Assert.False(metric.HasResetText);
		});

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			weeklyMetrics.Concat(
			[
				new UsageMetric(
					"agy.gemini.rolling-5h",
					"Gemini rolling 5h",
					40,
					"已使用 40%",
					now.AddHours(2)),
				new UsageMetric(
					"agy.claude.rolling-5h",
					"Claude rolling 5h",
					50,
					"已使用 50%",
					now.AddHours(3))
			]).ToArray(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			now,
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity));

		Assert.Equal(4, viewModel.UsageMetrics.Count);
		Assert.All(viewModel.UsageMetrics, metric => Assert.True(metric.HasUsageBar));
		Assert.DoesNotContain(
			viewModel.UsageMetrics,
			metric => metric.DisplayValue == "等待重新檢查");
	}

	[Fact]
	public void ApplySnapshot_WhenClaudeSubscriptionNeedsConfirmation_PreservesProfileAndClearsOldUsage()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: "original@example.com") with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		ApplyReadySnapshot(viewModel);
		Assert.True(viewModel.HasUsageMetrics);
		AccountProfile profileBeforeConfirmation = viewModel.Profile;
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			new DateTimeOffset(2026, 9, 5, 4, 0, 0, TimeSpan.Zero),
			Error: "尚未確認這張卡片的 Claude 訂閱。請使用原本的帳號，確認組織與訂閱方案。",
			RecoveryAction: UsageRecoveryAction.ConfirmSubscription));

		Assert.Same(profileBeforeConfirmation, viewModel.Profile);
		Assert.Equal("original@example.com", viewModel.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.NotConfigured, viewModel.StatusKind);
		Assert.Equal("需要確認訂閱", viewModel.PrimaryText);
		Assert.Equal("需要確認 Claude 訂閱", viewModel.RecoveryPanelTitle);
		Assert.Equal("確認 Claude 訂閱", viewModel.RecoveryActionText);
		Assert.Equal("確認 Claude 訂閱", viewModel.ClaudeAccountActionText);
		Assert.Contains(
			nameof(AccountUsageViewModel.ClaudeAccountActionText),
			changedProperties);
		Assert.True(viewModel.CanExecuteRecoveryAction);
		Assert.Empty(viewModel.UsageMetrics);
		Assert.Equal(0, viewModel.UsedPercent);
		Assert.DoesNotContain("損壞", viewModel.SecondaryText, StringComparison.Ordinal);
		Assert.DoesNotContain("切換", viewModel.RecoveryActionDescription, StringComparison.Ordinal);

		changedProperties.Clear();
		ApplyReadySnapshot(viewModel);

		Assert.Equal(AccountStatusKind.Ready, viewModel.StatusKind);
		Assert.Equal("切換 Claude 帳號", viewModel.ClaudeAccountActionText);
		Assert.Contains(
			nameof(AccountUsageViewModel.ClaudeAccountActionText),
			changedProperties);
		Assert.False(viewModel.HasRecoveryAction);
	}

	[Theory]
	[InlineData(ProviderKind.Claude, true)]
	[InlineData(ProviderKind.Codex, false)]
	[InlineData(ProviderKind.Copilot, false)]
	[InlineData(ProviderKind.Grok, false)]
	[InlineData(ProviderKind.Antigravity, false)]
	public void ApplySnapshot_WithSubscriptionConfirmation_OnlyClaudeOffersExecutableRecovery(
		ProviderKind provider,
		bool expectedExecutable)
	{
		AccountProfile profile = CreateProfile(provider) with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			new DateTimeOffset(2026, 9, 5, 4, 0, 0, TimeSpan.Zero),
			RecoveryAction: UsageRecoveryAction.ConfirmSubscription));

		Assert.Equal(expectedExecutable, viewModel.HasExecutableRecoveryAction);
		Assert.Equal(expectedExecutable, viewModel.CanExecuteRecoveryAction);
	}

	[Fact]
	public void ApplySnapshot_WhenClaudeConsentIsRequired_UsesConfirmationWording()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			RecoveryAction: UsageRecoveryAction.ConnectAccount));

		Assert.True(viewModel.RequiresClaudeQuotaRiskConsent);
		Assert.Equal("等待確認用量讀取", viewModel.PrimaryText);
		Assert.Equal("需要確認用量讀取", viewModel.RecoveryPanelTitle);
		Assert.Equal("確認 Claude 用量讀取", viewModel.RecoveryActionText);
		Assert.Equal(
			"Claude /usage 查詢狀態時也可能計入少量用量。確認一次後會自動更新；需要登入時會開啟 Claude 官方登入。",
			viewModel.RecoveryActionDescription);
		Assert.DoesNotContain(
			"重新連接",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		UsageRecoveryAction.RestartApplication,
		"重新啟動 AI Usage",
		false)]
	[InlineData(
		UsageRecoveryAction.InstallOrUpdate,
		"安裝或更新 Claude Code",
		false)]
	[InlineData(
		UsageRecoveryAction.ConnectAccount,
		"重新連接",
		false)]
	[InlineData(
		UsageRecoveryAction.ConfirmSubscription,
		"請使用原本的帳號，確認組織與訂閱方案",
		false)]
	[InlineData(
		UsageRecoveryAction.SwitchAccount,
		"切換至支援的 Claude 訂閱帳號",
		false)]
	[InlineData(
		UsageRecoveryAction.ReconfigureUsageSource,
		"帳號不必重新登入",
		false)]
	[InlineData(UsageRecoveryAction.Retry, "自動再試", true)]
	[InlineData(UsageRecoveryAction.None, "自動再試", true)]
	public void GetClaudeLoginCompletionNotice_UsesRecoveryActionBeforeStatus(
		UsageRecoveryAction recoveryAction,
		string expectedMessageFragment,
		bool expectsAutomaticRecovery)
	{
		AccountProfile profile = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Official,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			RecoveryAction: recoveryAction,
			ProviderAccountIdentity: "claude@example.com"));

		(string message, string caption, System.Windows.MessageBoxImage image) =
			AccountConnectionCoordinator.GetClaudeLoginCompletionNotice(viewModel);

		Assert.Contains(expectedMessageFragment, message, StringComparison.Ordinal);
		Assert.Equal(
			expectsAutomaticRecovery,
			message.Contains("自動", StringComparison.Ordinal));
		Assert.False(string.IsNullOrWhiteSpace(caption));
		Assert.Equal(System.Windows.MessageBoxImage.Warning, image);
		if (recoveryAction == UsageRecoveryAction.ConfirmSubscription)
		{
			Assert.Equal("需要確認 Claude 訂閱", caption);
		}
	}

	[Fact]
	public void ApplySnapshot_WithoutRecoveryAction_HidesRecoveryGuidance()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Official,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(UsageRecoveryAction.None, viewModel.RecoveryAction);
		Assert.False(viewModel.HasRecoveryAction);
		Assert.Equal("用量狀態", viewModel.RecoveryPanelTitle);
		Assert.False(viewModel.HasExecutableRecoveryAction);
		Assert.False(viewModel.CanExecuteRecoveryAction);
		Assert.Equal(string.Empty, viewModel.RecoveryActionText);
		Assert.Equal(string.Empty, viewModel.RecoveryActionDescription);
	}

	[Fact]
	public void ApplySnapshot_WithInformationalStaleNotice_UsesUsageStatusTitle()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Official,
			SnapshotStatus.Stale,
			DateTimeOffset.UtcNow,
			Error: "顯示上次確認的 Claude 用量。");

		viewModel.ApplySnapshot(snapshot);

		Assert.True(viewModel.HasNotice);
		Assert.False(viewModel.HasRecoveryAction);
		Assert.Equal("用量狀態", viewModel.RecoveryPanelTitle);
	}

	[Fact]
	public void ApplySnapshot_WithRetryableStaleData_UsesOneAutomaticRetryMessage()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: "person@example.com") with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = new(2026, 8, 2, 10, 18, 0, TimeSpan.Zero);
		UsageSnapshot snapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"claude.five_hour",
					"5 小時用量",
					22,
					"已使用 22%")
			},
			SourceTrust.Official,
			SnapshotStatus.Stale,
			observedAt,
			ObservedAt: observedAt,
			Error: "暫時無法讀取 Claude 用量，稍後會自動重試。",
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);

		viewModel.ApplySnapshot(snapshot);

		Assert.False(viewModel.HasNotice);
		Assert.Equal("稍後會自動再試", viewModel.RecoveryPanelTitle);
		Assert.Equal(
			$"目前顯示 {observedAt.ToLocalTime():yyyy/MM/dd HH:mm} 成功讀取的資料。" +
			"暫時無法讀取 Claude 用量。 AI Usage 稍後會自動再試，不需要手動操作。",
			viewModel.RecoveryActionDescription);
		Assert.Contains(
			"暫時無法讀取",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		string announcement = Assert.IsType<string>(
			AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel));
		Assert.Contains("自動再試", announcement, StringComparison.Ordinal);
		Assert.Equal(
			announcement.IndexOf("自動再試", StringComparison.Ordinal),
			announcement.LastIndexOf("自動再試", StringComparison.Ordinal));
	}

	[Fact]
	public void ApplySnapshot_WithSelfDescribingRetryableStaleData_DoesNotRequestUserAction()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: "person@example.com") with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = new(2026, 8, 22, 2, 1, 58, TimeSpan.Zero);
		UsageSnapshot snapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"claude.five_hour",
					"5 小時用量",
					0,
					"已使用 0%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			observedAt,
			ObservedAt: observedAt,
			Error: "Claude 暫時未回傳用量額度；AI Usage 稍後會自動再試，不需要操作。",
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);

		viewModel.ApplySnapshot(snapshot);

		Assert.False(viewModel.HasNotice);
		Assert.Equal("稍後會自動再試", viewModel.RecoveryPanelTitle);
		Assert.Equal(
			$"目前顯示 {observedAt.ToLocalTime():yyyy/MM/dd HH:mm} 成功讀取的資料。" +
			"Claude 暫時未回傳用量額度。 AI Usage 稍後會自動再試，不需要手動操作。",
			viewModel.RecoveryActionDescription);
		Assert.Contains(
			"Claude 暫時未回傳用量額度",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"訂閱或帳務狀態",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.Equal(
			viewModel.RecoveryActionDescription.IndexOf("自動再試", StringComparison.Ordinal),
			viewModel.RecoveryActionDescription.LastIndexOf("自動再試", StringComparison.Ordinal));
		string announcement = Assert.IsType<string>(
			AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel));
		Assert.Contains("不需要手動操作", announcement, StringComparison.Ordinal);
		Assert.DoesNotContain("訂閱或帳務狀態", announcement, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		"暫時無法讀取 Codex 用量，稍後會自動再試。 " +
		"CLI 版本診斷：偵測到 Codex CLI 0.145.0；本版相容性基準為 0.144.1。" +
		"這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。",
		"暫時無法讀取 Codex 用量。")]
	[InlineData(
		"上次 Codex 用量檢查逾時。" +
		"AI Usage 稍後會自動再試，並保留上次成功讀取的資料。 " +
		"CLI 版本診斷：偵測到 Codex CLI 0.145.0；本版相容性基準為 0.144.1。" +
		"這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。",
		"上次 Codex 用量檢查逾時。")]
	public void ApplySnapshot_WithVersionDiagnosticRetryableStaleData_PreservesReasonAndDiagnostic(
		string error,
		string expectedReason)
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Codex,
			providerAccountIdentity: "person@example.com");
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		DateTimeOffset observedAt = new(2026, 8, 22, 2, 1, 58, TimeSpan.Zero);
		UsageSnapshot snapshot = new(
			profile,
			new[]
			{
				new UsageMetric(
					"codex",
					"5 小時用量",
					10,
					"已使用 10%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			observedAt,
			ObservedAt: observedAt,
			Error: error,
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);

		viewModel.ApplySnapshot(snapshot);

		Assert.Contains(
			expectedReason,
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.Contains(
			"CLI 版本診斷：偵測到 Codex CLI 0.145.0",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.Contains(
			"本版相容性基準為 0.144.1",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"不需要操作",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"；。",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"。。",
			viewModel.RecoveryActionDescription,
			StringComparison.Ordinal);
		Assert.Equal(
			viewModel.RecoveryActionDescription.IndexOf("自動再試", StringComparison.Ordinal),
			viewModel.RecoveryActionDescription.LastIndexOf("自動再試", StringComparison.Ordinal));
	}

	[Fact]
	public void ApplySnapshot_WithInlineAutomaticRetryGuidance_KeepsRecoveryPanelHidden()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: "person@example.com") with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		const string Error =
			"Claude 暫時未回傳用量額度；AI Usage 稍後會自動再試，不需要操作。";

		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: Error,
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry));

		Assert.Equal(Error, viewModel.SecondaryText);
		Assert.False(viewModel.ShowRecoveryActionNotice);
	}

	[Fact]
	public void ApplySnapshot_WithEmptyRetryableStaleData_KeepsAutomaticRetryNotice()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: "person@example.com") with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		DateTimeOffset observedAt = DateTimeOffset.UtcNow.AddHours(-1);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Stale,
			observedAt,
			ObservedAt: observedAt,
			Error: "暫時無法讀取 Claude 用量，稍後會自動再試。",
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);

		viewModel.ApplySnapshot(snapshot);

		Assert.True(viewModel.HasNoUsageMetrics);
		Assert.True(viewModel.ShowRecoveryActionNotice);
		Assert.DoesNotContain(
			"自動再試",
			viewModel.SecondaryText,
			StringComparison.Ordinal);
		string announcement = Assert.IsType<string>(
			AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel));
		Assert.Contains("稍後會自動再試", announcement, StringComparison.Ordinal);
	}

	[Fact]
	public void ClaudeAccountActionText_WithKnownIdentity_OffersSwitchWithoutReloginWording()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Claude) with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);
		viewModel.SeedProviderAccountIdentity("person@example.com");

		Assert.Equal("切換 Claude 帳號", viewModel.ClaudeAccountActionText);
	}

	[Fact]
	public void ProviderAccountChange_DisablesRecoveryActionUntilConnectionFinishes()
	{
		AccountProfile profile = CreateProfile(ProviderKind.Claude);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Official,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			RecoveryAction: UsageRecoveryAction.RestartApplication));

		Assert.True(viewModel.CanExecuteRecoveryAction);

		viewModel.BeginProviderAccountChange();

		Assert.False(viewModel.CanExecuteRecoveryAction);

		viewModel.EndProviderAccountChange();

		Assert.True(viewModel.CanExecuteRecoveryAction);
	}

	[Theory]
	[InlineData(
		ProviderKind.Claude,
		"連接 Claude 帳號",
		"取消 Claude 帳號連接")]
	[InlineData(
		ProviderKind.Codex,
		"連接 Codex 帳號",
		"取消 Codex 帳號連接")]
	[InlineData(
		ProviderKind.Grok,
		"連接 Grok 帳號",
		"取消 Grok 帳號連接")]
	public void ProviderAccountChange_WithConnectionRecovery_KeepsCancelActionInvokable(
		ProviderKind provider,
		string connectionActionText,
		string cancellationActionText)
	{
		AccountProfile profile = CreateProfile(provider) with
		{
			HasAcceptedClaudeQuotaRisk = provider == ProviderKind.Claude
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			RecoveryAction: UsageRecoveryAction.ConnectAccount));

		Assert.True(viewModel.CanExecuteRecoveryAction);
		Assert.True(viewModel.CanInvokeRecoveryAction);
		Assert.Equal(connectionActionText, viewModel.RecoveryActionText);

		viewModel.BeginProviderAccountChange();

		Assert.False(viewModel.CanExecuteRecoveryAction);
		Assert.True(viewModel.CanInvokeRecoveryAction);
		Assert.Equal(cancellationActionText, viewModel.RecoveryActionText);

		viewModel.EndProviderAccountChange();

		Assert.True(viewModel.CanExecuteRecoveryAction);
		Assert.True(viewModel.CanInvokeRecoveryAction);
		Assert.Equal(connectionActionText, viewModel.RecoveryActionText);
	}

	[Theory]
	[InlineData(
		ProviderKind.Claude,
		"連接 Claude 帳號",
		"正在完成 Claude 帳號連接")]
	[InlineData(
		ProviderKind.Codex,
		"連接 Codex 帳號",
		"正在完成 Codex 帳號連接")]
	[InlineData(
		ProviderKind.Grok,
		"連接 Grok 帳號",
		"正在完成 Grok 帳號連接")]
	public void ProviderAccountChangeCommit_DisablesCancellationUntilEndOrAbort(
		ProviderKind provider,
		string connectionActionText,
		string commitActionText)
	{
		AccountProfile profile = CreateProfile(provider) with
		{
			HasAcceptedClaudeQuotaRisk = provider == ProviderKind.Claude
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			RecoveryAction: UsageRecoveryAction.ConnectAccount));
		viewModel.BeginProviderAccountChange();
		bool showsDefaultConnectionAction = provider switch
		{
			ProviderKind.Claude =>
				viewModel.ShowClaudeDefaultConnectionAction,
			ProviderKind.Codex =>
				viewModel.ShowCodexDefaultConnectionAction,
			ProviderKind.Grok =>
				viewModel.ShowGrokDefaultConnectionAction,
			_ => false
		};

		Assert.True(viewModel.HasExecutableRecoveryAction);
		Assert.False(showsDefaultConnectionAction);

		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());

		Assert.True(viewModel.IsProviderAccountChangeInProgress);
		Assert.True(viewModel.IsProviderAccountChangeCommitInProgress);
		Assert.False(viewModel.CanCancelProviderAccountConnection);
		Assert.False(viewModel.CanInvokeProviderAccountAction);
		Assert.False(viewModel.CanInvokeRecoveryAction);
		Assert.Equal(commitActionText, viewModel.RecoveryActionText);
		Assert.Equal(
			commitActionText,
			provider == ProviderKind.Claude
				? viewModel.ClaudeAccountActionText
				: provider == ProviderKind.Codex
					? viewModel.CodexAccountActionText
					: viewModel.GrokAccountActionText);
		Assert.False(viewModel.TryBeginProviderAccountChangeCommit());

		viewModel.EndProviderAccountChange();

		Assert.False(viewModel.IsProviderAccountChangeInProgress);
		Assert.False(viewModel.IsProviderAccountChangeCommitInProgress);
		Assert.True(viewModel.CanConnectProviderAccount);
		Assert.True(viewModel.CanInvokeProviderAccountAction);
		Assert.True(viewModel.CanInvokeRecoveryAction);
		Assert.Equal(connectionActionText, viewModel.RecoveryActionText);

		viewModel.BeginProviderAccountChange();
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());

		viewModel.AbortProviderAccountChange();

		Assert.False(viewModel.IsProviderAccountChangeInProgress);
		Assert.False(viewModel.IsProviderAccountChangeCommitInProgress);
		Assert.True(viewModel.CanConnectProviderAccount);
		Assert.True(viewModel.CanInvokeProviderAccountAction);
		Assert.True(viewModel.CanInvokeRecoveryAction);
		Assert.Equal(connectionActionText, viewModel.RecoveryActionText);
	}

	[Theory]
	[InlineData(ProviderKind.Claude, false)]
	[InlineData(ProviderKind.Claude, true)]
	[InlineData(ProviderKind.Codex, false)]
	[InlineData(ProviderKind.Codex, true)]
	[InlineData(ProviderKind.Grok, false)]
	[InlineData(ProviderKind.Grok, true)]
	public void ProviderAccountChange_WithoutExecutableRecovery_ShowsInlineCancel(
		ProviderKind provider,
		bool hasPassiveRetry)
	{
		const string ProviderAccountIdentity = "person@example.com";
		AccountProfile profile = CreateProfile(
			provider,
			providerAccountIdentity: ProviderAccountIdentity) with
		{
			HasAcceptedClaudeQuotaRisk = provider == ProviderKind.Claude
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		if (hasPassiveRetry)
		{
			viewModel.ApplySnapshot(new UsageSnapshot(
				profile,
				Array.Empty<UsageMetric>(),
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Stale,
				DateTimeOffset.UtcNow,
				ProviderAccountIdentity: ProviderAccountIdentity,
				RecoveryAction: UsageRecoveryAction.Retry));
		}
		else
		{
			ApplyReadySnapshot(viewModel);
		}

		bool ShowDefaultConnectionAction()
		{
			return provider switch
			{
				ProviderKind.Claude =>
					viewModel.ShowClaudeDefaultConnectionAction,
				ProviderKind.Codex =>
					viewModel.ShowCodexDefaultConnectionAction,
				ProviderKind.Grok =>
					viewModel.ShowGrokDefaultConnectionAction,
				_ => false
			};
		}

		string GetActionText()
		{
			return provider switch
			{
				ProviderKind.Claude => viewModel.ClaudeAccountActionText,
				ProviderKind.Codex => viewModel.CodexAccountActionText,
				ProviderKind.Grok => viewModel.GrokAccountActionText,
				_ => string.Empty
			};
		}

		string showPropertyName = provider switch
		{
			ProviderKind.Claude =>
				nameof(AccountUsageViewModel.ShowClaudeDefaultConnectionAction),
			ProviderKind.Codex =>
				nameof(AccountUsageViewModel.ShowCodexDefaultConnectionAction),
			ProviderKind.Grok =>
				nameof(AccountUsageViewModel.ShowGrokDefaultConnectionAction),
			_ => string.Empty
		};
		string providerName = provider switch
		{
			ProviderKind.Claude => "Claude",
			ProviderKind.Codex => "Codex",
			ProviderKind.Grok => "Grok",
			_ => string.Empty
		};
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		Assert.False(ShowDefaultConnectionAction());
		Assert.False(viewModel.HasExecutableRecoveryAction);

		viewModel.BeginProviderAccountChange();

		Assert.True(viewModel.CanCancelProviderAccountConnection);
		Assert.True(ShowDefaultConnectionAction());
		Assert.Equal($"取消 {providerName} 帳號連接", GetActionText());
		Assert.Contains(showPropertyName, changedProperties);

		changedProperties.Clear();
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());

		Assert.False(viewModel.CanCancelProviderAccountConnection);
		Assert.False(ShowDefaultConnectionAction());
		Assert.Contains(showPropertyName, changedProperties);

		viewModel.AbortProviderAccountChange();
	}

	[Theory]
	[InlineData(
		ProviderKind.Claude,
		SnapshotStatus.NotConfigured,
		UsageRecoveryAction.ConnectAccount)]
	[InlineData(
		ProviderKind.Claude,
		SnapshotStatus.Unsupported,
		UsageRecoveryAction.InstallOrUpdate)]
	[InlineData(
		ProviderKind.Codex,
		SnapshotStatus.NotConfigured,
		UsageRecoveryAction.ConnectAccount)]
	[InlineData(
		ProviderKind.Codex,
		SnapshotStatus.Unsupported,
		UsageRecoveryAction.InstallOrUpdate)]
	[InlineData(
		ProviderKind.Grok,
		SnapshotStatus.NotConfigured,
		UsageRecoveryAction.ConnectAccount)]
	[InlineData(
		ProviderKind.Grok,
		SnapshotStatus.Unsupported,
		UsageRecoveryAction.InstallOrUpdate)]
	public void AuthenticatedProviderCommit_TransitionalSnapshotKeepsExpectedIdentity(
		ProviderKind provider,
		SnapshotStatus transitionalStatus,
		UsageRecoveryAction transitionalRecoveryAction)
	{
		const string VerifiedIdentity = "connected@example.com";
		const string UnexpectedIdentity = "different@example.com";
		AccountProfile initialProfile = CreateProfile(provider) with
		{
			HasAcceptedClaudeQuotaRisk = provider == ProviderKind.Claude
		};
		AccountUsageViewModel viewModel = new(
			initialProfile,
			canManage: true);
		viewModel.BeginProviderAccountChange();
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());
		AccountProfile committedProfile = initialProfile with
		{
			ProviderAccountIdentity = VerifiedIdentity
		};
		viewModel.ApplyProfile(committedProfile, canManage: true);
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		viewModel.PrepareAuthenticatedProviderConnectionRefresh();

		viewModel.ApplySnapshot(new UsageSnapshot(
			committedProfile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			transitionalStatus,
			DateTimeOffset.UtcNow,
			Error: "Synthetic transitional result.",
			RecoveryAction: transitionalRecoveryAction));
		viewModel.ApplySnapshot(new UsageSnapshot(
			committedProfile,
			new[]
			{
				new UsageMetric(
					"session",
					"Session",
					25,
					"Used 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: UnexpectedIdentity));
		viewModel.CompleteProviderAccountChange();

		UsageSnapshot currentSnapshot = Assert.IsType<UsageSnapshot>(
			viewModel.CurrentSnapshot);
		Assert.Equal(
			VerifiedIdentity,
			viewModel.Profile.ProviderAccountIdentity);
		Assert.Equal(VerifiedIdentity, viewModel.ProviderAccountIdentity);
		Assert.Equal(
			VerifiedIdentity,
			currentSnapshot.ProviderAccountIdentity);
		Assert.Equal(SnapshotStatus.Error, currentSnapshot.Status);
		Assert.Equal(
			UsageRecoveryAction.SwitchAccount,
			currentSnapshot.RecoveryAction);
		Assert.True(viewModel.DidRejectProviderAccountSnapshot);
		Assert.False(viewModel.IsProviderAccountChangeInProgress);
	}

	[Theory]
	[InlineData(ProviderKind.Claude)]
	[InlineData(ProviderKind.Codex)]
	[InlineData(ProviderKind.Grok)]
	public void PrepareAuthenticatedProviderConnectionRefresh_ProjectsInformationalPendingState(
		ProviderKind provider)
	{
		const string VerifiedIdentity = "connected@example.com";
		AccountProfile initialProfile = CreateProfile(provider) with
		{
			HasAcceptedClaudeQuotaRisk = provider == ProviderKind.Claude
		};
		AccountUsageViewModel viewModel = new(initialProfile, canManage: true);
		viewModel.BeginProviderAccountChange();
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());
		AccountProfile committedProfile = initialProfile with
		{
			ProviderAccountIdentity = VerifiedIdentity
		};
		viewModel.ApplyProfile(committedProfile, canManage: true);
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);

		viewModel.PrepareAuthenticatedProviderConnectionRefresh();

		UsageSnapshot snapshot = Assert.IsType<UsageSnapshot>(
			viewModel.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Refreshing, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(VerifiedIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Refreshing, viewModel.StatusKind);
		Assert.Equal(AccountStatusSeverity.Informational, viewModel.StatusSeverity);
		Assert.Equal(
			AccountStatusSeverity.Informational,
			viewModel.DisplayStatusSeverity);
		Assert.Equal("檢查中", viewModel.StatusText);
		Assert.Equal("連接中", viewModel.DisplayStatusText);
		Assert.Equal("正在讀取用量", viewModel.PrimaryText);
		Assert.Contains(
			"正在重新檢查用量",
			viewModel.SecondaryText,
			StringComparison.Ordinal);
		Assert.True(viewModel.IsProviderAccountChangeInProgress);
		Assert.True(viewModel.IsProviderAccountChangeCommitInProgress);
		Assert.False(viewModel.HasExecutableRecoveryAction);
		Assert.False(viewModel.ShowRecoveryActionNotice);
		Assert.Equal(
			$"帳號「{viewModel.AccountName}」：連接中。",
			AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel));

		viewModel.CompleteProviderAccountChange();

		snapshot = Assert.IsType<UsageSnapshot>(viewModel.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.False(viewModel.IsProviderAccountChangeInProgress);
		Assert.Equal("讀取失敗", viewModel.PrimaryText);
		Assert.Contains(
			"AI Usage 稍後會自動再試，不需要操作",
			viewModel.SecondaryText,
			StringComparison.Ordinal);
		Assert.DoesNotContain("正在", viewModel.SecondaryText, StringComparison.Ordinal);
		Assert.False(viewModel.ShowRecoveryActionNotice);
		string announcement = Assert.IsType<string>(
			AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel));
		Assert.Contains(
			"AI Usage 稍後會自動再試，不需要操作",
			announcement,
			StringComparison.Ordinal);
		Assert.Equal(
			announcement.IndexOf("稍後會自動再試", StringComparison.Ordinal),
			announcement.LastIndexOf("稍後會自動再試", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(ProviderKind.Claude)]
	[InlineData(ProviderKind.Codex)]
	[InlineData(ProviderKind.Grok)]
	public void AuthenticatedProviderConnectionRefresh_WithErrorResult_DefersFailureProjectionUntilCompletion(
		ProviderKind provider)
	{
		const string VerifiedIdentity = "connected@example.com";
		const string Error =
			"暫時無法讀取用量；AI Usage 稍後會自動再試，不需要操作。";
		AccountProfile initialProfile = CreateProfile(provider) with
		{
			HasAcceptedClaudeQuotaRisk = provider == ProviderKind.Claude
		};
		AccountUsageViewModel viewModel = new(initialProfile, canManage: true);
		viewModel.BeginProviderAccountChange();
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());
		AccountProfile committedProfile = initialProfile with
		{
			ProviderAccountIdentity = VerifiedIdentity
		};
		viewModel.ApplyProfile(committedProfile, canManage: true);
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		viewModel.PrepareAuthenticatedProviderConnectionRefresh();
		string pendingSecondaryText = viewModel.SecondaryText;

		viewModel.ApplySnapshot(new UsageSnapshot(
			committedProfile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: Error,
			ProviderAccountIdentity: VerifiedIdentity,
			RecoveryAction: UsageRecoveryAction.Retry));

		Assert.Equal(SnapshotStatus.Error, viewModel.CurrentSnapshot?.Status);
		Assert.Equal(UsageRecoveryAction.Retry, viewModel.RecoveryAction);
		Assert.Equal(AccountStatusKind.Refreshing, viewModel.StatusKind);
		Assert.Equal("正在讀取用量", viewModel.PrimaryText);
		Assert.Equal(pendingSecondaryText, viewModel.SecondaryText);
		Assert.False(viewModel.ShowRecoveryActionNotice);

		viewModel.CompleteProviderAccountChange();

		Assert.False(viewModel.IsProviderAccountChangeInProgress);
		Assert.Equal(AccountStatusKind.Error, viewModel.StatusKind);
		Assert.Equal("讀取失敗", viewModel.PrimaryText);
		Assert.Equal(Error, viewModel.SecondaryText);
		Assert.False(viewModel.ShowRecoveryActionNotice);
	}

	[Fact]
	public void AuthenticatedProviderConnectionRefresh_WithTransitionalNotConfigured_DefersRecoveryUntilCompletion()
	{
		const string VerifiedIdentity = "connected@example.com";
		AccountProfile initialProfile = CreateProfile(ProviderKind.Claude) with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(initialProfile, canManage: true);
		viewModel.BeginProviderAccountChange();
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());
		AccountProfile committedProfile = initialProfile with
		{
			ProviderAccountIdentity = VerifiedIdentity
		};
		viewModel.ApplyProfile(committedProfile, canManage: true);
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		viewModel.PrepareAuthenticatedProviderConnectionRefresh();
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArguments) =>
			changedProperties.Add(eventArguments.PropertyName);

		viewModel.ApplySnapshot(new UsageSnapshot(
			committedProfile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			Error: "登入狀態尚未同步完成。",
			RecoveryAction: UsageRecoveryAction.ConnectAccount));

		Assert.Equal(SnapshotStatus.NotConfigured, viewModel.CurrentSnapshot?.Status);
		Assert.Equal(UsageRecoveryAction.ConnectAccount, viewModel.RecoveryAction);
		Assert.Equal(AccountStatusKind.Refreshing, viewModel.StatusKind);
		Assert.Equal("正在讀取用量", viewModel.PrimaryText);
		Assert.False(viewModel.ShowRecoveryActionNotice);
		changedProperties.Clear();

		viewModel.CompleteProviderAccountChange();

		Assert.Equal(AccountStatusKind.NotConfigured, viewModel.StatusKind);
		Assert.Equal("尚未連接", viewModel.PrimaryText);
		Assert.Equal("登入狀態尚未同步完成。", viewModel.SecondaryText);
		Assert.True(viewModel.ShowRecoveryActionNotice);
		Assert.Contains(
			nameof(AccountUsageViewModel.ShowRecoveryActionNotice),
			changedProperties);
	}

	[Fact]
	public void AuthenticatedProviderConnectionRefresh_WithReadyResult_DefersMetricsProjectionUntilCompletion()
	{
		const string VerifiedIdentity = "connected@example.com";
		AccountProfile initialProfile = CreateProfile(ProviderKind.Codex);
		AccountUsageViewModel viewModel = new(initialProfile, canManage: true);
		viewModel.BeginProviderAccountChange();
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());
		AccountProfile committedProfile = initialProfile with
		{
			ProviderAccountIdentity = VerifiedIdentity
		};
		viewModel.ApplyProfile(committedProfile, canManage: true);
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		viewModel.PrepareAuthenticatedProviderConnectionRefresh();

		viewModel.ApplySnapshot(new UsageSnapshot(
			committedProfile,
			new[]
			{
				new UsageMetric(
					"codex",
					"5 小時用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: VerifiedIdentity));

		Assert.Equal(SnapshotStatus.Ready, viewModel.CurrentSnapshot?.Status);
		Assert.True(viewModel.HasConfirmedProviderAccountBinding);
		Assert.Equal(AccountStatusKind.Refreshing, viewModel.StatusKind);
		Assert.Empty(viewModel.UsageMetrics);
		viewModel.SetUsageDisplayMode(UsageDisplayMode.Remaining);
		Assert.Empty(viewModel.UsageMetrics);

		viewModel.CompleteProviderAccountChange();

		Assert.Equal(AccountStatusKind.Ready, viewModel.StatusKind);
		UsageMetricViewModel metric = Assert.Single(viewModel.UsageMetrics);
		Assert.Equal("剩餘 75%", metric.DisplayValue);
	}

	[Fact]
	public void EndProviderAccountChange_WithUnrelatedRetryingRefresh_KeepsRefreshingState()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: "person@example.com") with
		{
			HasAcceptedClaudeQuotaRisk = true
		};
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		UsageSnapshot snapshot = new(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.Refreshing,
			DateTimeOffset.UtcNow,
			Error: "正在檢查用量。",
			ProviderAccountIdentity: profile.ProviderAccountIdentity,
			RecoveryAction: UsageRecoveryAction.Retry);
		viewModel.ApplySnapshot(snapshot);
		viewModel.BeginProviderAccountChange();

		viewModel.EndProviderAccountChange();

		UsageSnapshot currentSnapshot = Assert.IsType<UsageSnapshot>(
			viewModel.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Refreshing, currentSnapshot.Status);
		Assert.Equal("正在檢查用量。", currentSnapshot.Error);
	}

	[Fact]
	public void PrepareAntigravityConnectionRefresh_ProjectsInformationalPendingState()
	{
		const string VerifiedIdentity =
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity;
		AccountProfile initialProfile = CreateProfile(ProviderKind.Antigravity);
		AccountUsageViewModel viewModel = new(initialProfile, canManage: true);
		viewModel.BeginProviderAccountChange();
		viewModel.SeedProviderAccountIdentity(VerifiedIdentity);
		Assert.True(viewModel.TryBeginProviderAccountChangeCommit());
		AccountProfile committedProfile = initialProfile with
		{
			ProviderAccountIdentity = VerifiedIdentity
		};
		viewModel.ApplyProfile(committedProfile, canManage: true);

		viewModel.PrepareAntigravityConnectionRefresh();

		UsageSnapshot snapshot = Assert.IsType<UsageSnapshot>(
			viewModel.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Refreshing, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.Equal(VerifiedIdentity, snapshot.ProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Refreshing, viewModel.StatusKind);
		Assert.Equal(AccountStatusSeverity.Informational, viewModel.StatusSeverity);
		Assert.Equal(
			AccountStatusSeverity.Informational,
			viewModel.DisplayStatusSeverity);
		Assert.Equal("檢查中", viewModel.StatusText);
		Assert.Equal("連接中", viewModel.DisplayStatusText);
		Assert.Equal("正在讀取用量", viewModel.PrimaryText);
		Assert.Contains(
			"正在重新讀取用量",
			viewModel.SecondaryText,
			StringComparison.Ordinal);
		Assert.True(viewModel.IsProviderAccountChangeInProgress);
		Assert.True(viewModel.IsProviderAccountChangeCommitInProgress);
		Assert.False(viewModel.HasExecutableRecoveryAction);
		Assert.False(viewModel.ShowRecoveryActionNotice);

		viewModel.CompleteAntigravityConnection();

		snapshot = Assert.IsType<UsageSnapshot>(viewModel.CurrentSnapshot);
		Assert.Equal(SnapshotStatus.Error, snapshot.Status);
		Assert.Equal(UsageRecoveryAction.Retry, snapshot.RecoveryAction);
		Assert.False(viewModel.IsProviderAccountChangeInProgress);
		Assert.Equal("讀取失敗", viewModel.PrimaryText);
		Assert.Contains(
			"AI Usage 稍後會自動再試，不需要操作",
			viewModel.SecondaryText,
			StringComparison.Ordinal);
		Assert.DoesNotContain("正在", viewModel.SecondaryText, StringComparison.Ordinal);
		Assert.False(viewModel.ShowRecoveryActionNotice);
	}

	[Theory]
	[InlineData(
		ProviderKind.Claude,
		true,
		AccountStatusKind.NotConnected,
		AccountStatusSeverity.Neutral,
		"尚未連接")]
	[InlineData(
		ProviderKind.Claude,
		false,
		AccountStatusKind.Disabled,
		AccountStatusSeverity.Neutral,
		"已停止檢查")]
	[InlineData(
		ProviderKind.Antigravity,
		true,
		AccountStatusKind.NotConnected,
		AccountStatusSeverity.Neutral,
		"尚未連接")]
	public void Constructor_ProjectsLocalSemanticStatus(
		ProviderKind provider,
		bool isEnabled,
		AccountStatusKind expectedKind,
		AccountStatusSeverity expectedSeverity,
		string expectedText)
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(provider, isEnabled),
			canManage: true);

		Assert.Equal(expectedKind, viewModel.StatusKind);
		Assert.Equal(expectedSeverity, viewModel.StatusSeverity);
		Assert.Equal(expectedText, viewModel.StatusText);
	}

	[Fact]
	public void Constructor_WithAntigravity_UsesCommonPendingIdentityText()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Antigravity),
			canManage: true);

		Assert.Null(viewModel.ProviderAccountIdentity);
		Assert.Equal("尚未確認", viewModel.AccountDisplayText);
		Assert.Equal("尚未取得用量", viewModel.PrimaryText);
		Assert.Equal("等待本機 Antigravity CLI 檢查完成", viewModel.SecondaryText);
	}

	[Fact]
	public void Constructor_WithBoundAntigravity_WaitsForAutomaticRefreshWithoutReconnectAction()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Antigravity,
				providerAccountIdentity:
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity),
			canManage: true);

		Assert.True(viewModel.HasProviderAccountIdentity);
		Assert.Equal(AccountStatusKind.Refreshing, viewModel.StatusKind);
		Assert.Equal("正在檢查用量", viewModel.PrimaryText);
		Assert.False(viewModel.ShowAntigravityDefaultConnectionAction);
	}

	[Fact]
	public void AntigravityCommitPending_WhenDisabledThenEnabled_RemainsAutomaticOnly()
	{
		AccountProfile disabledProfile = CreateProfile(
			ProviderKind.Antigravity) with { IsEnabled = false };
		AccountUsageViewModel viewModel = new(disabledProfile, canManage: true);

		viewModel.MarkOfficialAntigravityConnectionCommitPending();
		viewModel.ApplyProfile(
			disabledProfile with { IsEnabled = true },
			canManage: true);
		viewModel.SetAntigravityAccountSetupAvailability(true);

		Assert.False(viewModel.CanConnectProviderAccount);
		Assert.False(viewModel.CanConnectProviderAccountAfterSave);
		Assert.False(viewModel.CanQueryUsage);
		Assert.False(viewModel.CanRefreshUsage);
		Assert.False(viewModel.CanExecuteRecoveryAction);
		Assert.False(viewModel.CanInvokeRecoveryAction);
		Assert.False(viewModel.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(UsageRecoveryAction.Retry, viewModel.RecoveryAction);
		Assert.Contains(
			"正在儲存連接資料",
			viewModel.CurrentSnapshot?.Error,
			StringComparison.Ordinal);
	}

	[Fact]
	public void AntigravityJournalCheckPending_BlocksSetupAndUsageUntilCleared()
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Antigravity) with
			{
				ProviderAccountIdentity =
					AntigravityOfficialPrintUsageClient.LocalSessionIdentity
			};
		AccountUsageViewModel viewModel = new(profile, canManage: true);

		Assert.True(viewModel.CanConnectProviderAccount);
		Assert.True(viewModel.CanQueryUsage);
		Assert.True(viewModel.CanRefreshUsage);

		viewModel.MarkAntigravityConnectionJournalCheckPending();

		Assert.False(viewModel.CanConnectProviderAccount);
		Assert.False(viewModel.CanQueryUsage);
		Assert.False(viewModel.CanRefreshUsage);
		Assert.False(viewModel.CanExecuteRecoveryAction);
		Assert.False(viewModel.CanInvokeRecoveryAction);
		Assert.Equal(UsageRecoveryAction.Retry, viewModel.RecoveryAction);

		viewModel.ClearAntigravityConnectionJournalCheckPending();

		Assert.True(viewModel.CanConnectProviderAccount);
		Assert.True(viewModel.CanQueryUsage);
		Assert.True(viewModel.CanRefreshUsage);
		Assert.Equal(UsageRecoveryAction.None, viewModel.RecoveryAction);
	}

	[Fact]
	public void SeedProviderAccountIdentity_NotifiesAntigravityDefaultActionVisibility()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Antigravity),
			canManage: true);
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArguments) =>
			changedProperties.Add(eventArguments.PropertyName);

		viewModel.SeedProviderAccountIdentity(
			AntigravityOfficialPrintUsageClient.LocalSessionIdentity);

		Assert.Contains(
			nameof(AccountUsageViewModel.ShowAntigravityDefaultConnectionAction),
			changedProperties);
	}

	[Fact]
	public void AntigravityConnectionActions_UseReconnectLanguage()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Antigravity),
			canManage: true);

		Assert.True(viewModel.IsAntigravity);
		Assert.True(viewModel.ShowAntigravityDefaultConnectionAction);
		Assert.Equal(
			"連接 Antigravity 帳號",
			viewModel.AntigravityAccountActionText);

		viewModel.ApplySnapshot(new UsageSnapshot(
			viewModel.Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			Error: "尚未連接。",
			RecoveryAction: UsageRecoveryAction.ReconfigureUsageSource));

		Assert.False(viewModel.ShowAntigravityDefaultConnectionAction);
		Assert.Equal("連接 Antigravity 帳號", viewModel.RecoveryActionText);
		Assert.Equal(
			"完成 Antigravity 帳號連接後，AI Usage 會自動更新用量。",
			viewModel.RecoveryActionDescription);

		viewModel.SeedProviderAccountIdentity("agy@example.com");

		Assert.Equal(
			"重新連接 Antigravity 帳號",
			viewModel.AntigravityAccountActionText);
		Assert.Equal("重新連接 Antigravity 帳號", viewModel.RecoveryActionText);
		Assert.Equal(
			"AI Usage 會重新確認目前的 Antigravity 帳號與用量讀取；既有 Antigravity 登入不受影響。",
			viewModel.RecoveryActionDescription);
	}

	[Fact]
	public void Constructor_WithConsentedClaude_KeepsInitialConnectionActionVisible()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Claude) with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);

		Assert.True(viewModel.ShowClaudeDefaultConnectionAction);
		Assert.Equal("連接 Claude 帳號", viewModel.ClaudeAccountActionText);
	}

	[Fact]
	public void ApplyProfile_WhenDisabled_PreservesLastConfirmedProviderIdentity()
	{
		const string AccountIdentity = "person@example.com";
		AccountProfile enabledProfile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: AccountIdentity);
		AccountUsageViewModel viewModel = new(enabledProfile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			enabledProfile,
			new[]
			{
				new UsageMetric(
					"session",
					"5 小時用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: AccountIdentity));

		viewModel.ApplyProfile(
			enabledProfile with { IsEnabled = false },
			canManage: true);

		Assert.Equal(AccountStatusKind.Disabled, viewModel.StatusKind);
		Assert.Equal(AccountIdentity, viewModel.ProviderAccountIdentity);
		Assert.Equal(
			$"{AccountIdentity}（上次確認）",
			viewModel.AccountDisplayText);
	}

	[Fact]
	public void Constructor_WhenDisabled_ExplainsThatIdentityWasNotChecked()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Claude, isEnabled: false),
			canManage: true);

		Assert.Equal(
			"未檢查（已停止檢查）",
			viewModel.AccountDisplayText);
	}

	[Theory]
	[InlineData(
		ProviderKind.Claude,
		UsageRecoveryAction.InstallOrUpdate,
		"CLI 待安裝或更新")]
	[InlineData(
		ProviderKind.Antigravity,
		UsageRecoveryAction.ReconfigureUsageSource,
		"需要確認用量讀取")]
	public void ApplySnapshot_WhenLocalUsageSourceNeedsAttention_PreservesIdentity(
		ProviderKind provider,
		UsageRecoveryAction recoveryAction,
		string expectedPrimaryText)
	{
		const string AccountIdentity = "person@example.com";
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				provider,
				providerAccountIdentity: AccountIdentity),
			canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			viewModel.Profile,
			new[]
			{
				new UsageMetric(
					"session",
					"5 小時用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: AccountIdentity));

		viewModel.ApplySnapshot(new UsageSnapshot(
			viewModel.Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			Error: "本機用量來源需要處理。",
			RecoveryAction: recoveryAction));

		Assert.Equal(AccountIdentity, viewModel.ProviderAccountIdentity);
		Assert.Equal(
			$"{AccountIdentity}（上次確認）",
			viewModel.AccountDisplayText);
		Assert.Equal(expectedPrimaryText, viewModel.PrimaryText);
	}

	[Fact]
	public void ApplySnapshot_WhenAccountConnectionIsRequired_PreservesLastConfirmedIdentity()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com") with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);
		ApplyReadySnapshot(viewModel);

		viewModel.ApplySnapshot(new UsageSnapshot(
			viewModel.Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			SnapshotStatus.NotConfigured,
			DateTimeOffset.UtcNow,
			Error: "登入已失效。",
			RecoveryAction: UsageRecoveryAction.ConnectAccount));

		Assert.Equal("person@example.com", viewModel.ProviderAccountIdentity);
		Assert.Equal(
			"person@example.com（上次確認）",
			viewModel.AccountDisplayText);
		Assert.Equal("尚未連接", viewModel.PrimaryText);
	}

	[Fact]
	public void ApplySnapshot_WithAntigravityIdentity_UsesCommonIdentityProjection()
	{
		const string AccountIdentity = "agy@example.invalid";
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Antigravity,
				providerAccountIdentity: AccountIdentity),
			canManage: true);
		UsageSnapshot snapshot = new(
			viewModel.Profile,
			new[]
			{
				new UsageMetric(
					"agy.gemini.weekly",
					"Gemini weekly",
					25,
					"已使用 25%")
			},
			SourceTrust.PrivateExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: AccountIdentity);

		Assert.Equal(AccountIdentity, viewModel.ProviderAccountIdentity);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(AccountIdentity, viewModel.ProviderAccountIdentity);
		Assert.Equal(
			AccountIdentity,
			viewModel.CurrentSnapshot?.ProviderAccountIdentity);
		Assert.Equal(
			AccountIdentity,
			viewModel.AccountDisplayText);
	}

	[Fact]
	public void ApplySnapshot_OrdersUsageWindowsByKindThenResetTime()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com"),
			canManage: true);
		UsageSnapshot snapshot = new(
			viewModel.Profile,
			new[]
			{
				new UsageMetric("weekly", "週用量", 10, "10%", now.AddMinutes(30)),
				new UsageMetric("other", "其他用量", 10, "10%", now.AddMinutes(10)),
				new UsageMetric("five_hour_later", "5小時用量", 10, "10%", now.AddHours(2)),
				new UsageMetric("five_hour_unknown", "5小時用量", 10, "10%"),
				new UsageMetric("five_hour_sooner", "5小時用量", 10, "10%", now.AddHours(1))
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			now,
			ProviderAccountIdentity:
				viewModel.Profile.ProviderAccountIdentity);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(
			new[]
			{
				"five_hour_sooner",
				"five_hour_later",
				"five_hour_unknown",
				"weekly",
				"other"
			},
			viewModel.UsageMetrics.Select(metric => metric.Key));
	}

	[Fact]
	public void ApplySnapshot_WithCodexAndSparkMetrics_OrdersCodexFirst()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Codex,
				providerAccountIdentity: "person@example.com"),
			canManage: true);
		UsageSnapshot snapshot = new(
			viewModel.Profile,
			new[]
			{
				new UsageMetric(
					"codex:codex:primary",
					"codex · 7 天用量",
					10,
					"10%",
					now.AddHours(2)),
				new UsageMetric(
					"codex:codex_bengalfox:primary",
					"GPT-5.3-Codex-Spark · 7 天用量",
					20,
					"20%",
					now.AddMinutes(30)),
				new UsageMetric(
					"codex:rate_limit_reset_credits",
					"可用重置次數",
					null,
					"2 次")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			now,
			ProviderAccountIdentity:
				viewModel.Profile.ProviderAccountIdentity);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(
			new[]
			{
				"codex:codex:primary",
				"codex:codex_bengalfox:primary",
				"codex:rate_limit_reset_credits"
			},
			viewModel.UsageMetrics.Select(metric => metric.Key));
	}

	[Fact]
	public void ApplySnapshot_WithCopilotMetrics_UsesFixedProviderOrder()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Copilot,
				providerAccountIdentity: "github:copilot:github.com/octocat"),
			canManage: true);
		UsageSnapshot snapshot = new(
			viewModel.Profile,
			new[]
			{
				new UsageMetric(
					"copilot-quota-completions",
					"Code completions",
					10,
					"200 / 2,000 已使用",
					now.AddMinutes(1)),
				new UsageMetric(
					"copilot-quota-code-reviews",
					"Code Reviews",
					10,
					"5 / 50 已使用",
					now.AddMinutes(2)),
				new UsageMetric(
					"copilot-quota-chat",
					"Chat requests",
					10,
					"20 / 200 已使用",
					now.AddMinutes(3)),
				new UsageMetric(
					"copilot-quota-premium-interactions",
					"Premium requests",
					10,
					"5 / 50 已使用",
					now.AddMinutes(4))
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			now,
			ProviderAccountIdentity:
				viewModel.Profile.ProviderAccountIdentity);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(
			new[]
			{
				"copilot-quota-premium-interactions",
				"copilot-quota-chat",
				"copilot-quota-completions",
				"copilot-quota-code-reviews"
			},
			viewModel.UsageMetrics.Select(metric => metric.Key));
		Assert.All(
			viewModel.UsageMetrics,
			metric => Assert.False(metric.HasResetText));
		Assert.All(
			viewModel.UsageMetrics,
			metric => Assert.Equal("已使用 10%", metric.DisplayValue));
		Assert.Equal(
			"5 / 50 已使用",
			viewModel.UsageMetrics[0].ToolTipValue);

		viewModel.SetUsageDisplayMode(UsageDisplayMode.Remaining);

		Assert.All(
			viewModel.UsageMetrics,
			metric => Assert.Equal("剩餘 90%", metric.DisplayValue));
		Assert.Equal(
			"5 / 50 已使用",
			viewModel.UsageMetrics[0].ToolTipValue);
	}

	[Theory]
	[InlineData(
		"5 / 50 已使用，額外用量已開啟")]
	[InlineData(
		"5 / 50 已使用，額度用完後仍可使用")]
	public void ApplySnapshot_WithCopilotUsageStatus_HidesProviderStatus(
		string displayValue)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Copilot,
				providerAccountIdentity: "github:copilot:github.com/octocat"),
			canManage: true);
		UsageSnapshot snapshot = new(
			viewModel.Profile,
			new[]
			{
				new UsageMetric(
					"copilot-quota-premium-interactions",
					"Premium requests",
					10,
					displayValue)
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			now,
			ProviderAccountIdentity:
				viewModel.Profile.ProviderAccountIdentity);

		viewModel.ApplySnapshot(snapshot);

		UsageMetricViewModel metric = Assert.Single(viewModel.UsageMetrics);
		Assert.Equal("已使用 10%", metric.DisplayValue);
		Assert.Equal("5 / 50 已使用", metric.ToolTipValue);
	}

	[Theory]
	[InlineData(ProviderKind.Claude)]
	[InlineData(ProviderKind.Antigravity)]
	public void ApplySnapshot_WithKnownProviderMetrics_UsesFixedProviderOrder(
		ProviderKind provider)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				provider,
				providerAccountIdentity: "person@example.com"),
			canManage: true);
		UsageMetric[] metrics = provider == ProviderKind.Claude
			?
			[
				new(
					"claude.rate_limit.seven_day.bucket.fable",
					"週用量（Fable）",
					10,
					"10%",
					now.AddMinutes(1)),
				new(
					"claude.rate_limit.seven_day.all_models",
					"週用量（所有模型）",
					10,
					"10%",
					now.AddHours(2)),
				new(
					"claude.rate_limit.five_hour",
					"5小時用量",
					10,
					"10%",
					now.AddHours(3))
			]
			:
			[
				new("agy.claude.weekly", "Claude weekly", 10, "10%", now.AddMinutes(1)),
				new("agy.claude.rolling-5h", "Claude rolling 5h", 10, "10%", now.AddMinutes(2)),
				new("agy.gemini.weekly", "Gemini weekly", 10, "10%", now.AddMinutes(3)),
				new("agy.gemini.rolling-5h", "Gemini rolling 5h", 10, "10%", now.AddHours(3))
			];
		UsageSnapshot snapshot = new(
			viewModel.Profile,
			metrics,
			SourceTrust.Official,
			SnapshotStatus.Ready,
			now,
			ProviderAccountIdentity:
				viewModel.Profile.ProviderAccountIdentity);

		viewModel.ApplySnapshot(snapshot);

		string[] expected = provider == ProviderKind.Claude
			?
			[
				"claude.rate_limit.five_hour",
				"claude.rate_limit.seven_day.all_models",
				"claude.rate_limit.seven_day.bucket.fable"
			]
			:
			[
				"agy.gemini.rolling-5h",
				"agy.gemini.weekly",
				"agy.claude.rolling-5h",
				"agy.claude.weekly"
			];
		Assert.Equal(expected, viewModel.UsageMetrics.Select(metric => metric.Key));
	}

	[Fact]
	public void ApplySnapshot_WhenResetTimeHasPassed_OrdersItAfterFutureReset()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com"),
			canManage: true);
		UsageSnapshot snapshot = new(
			viewModel.Profile,
			new[]
			{
				new UsageMetric("past", "5小時用量", 10, "10%", now.AddHours(-1)),
				new UsageMetric("future", "5小時用量", 10, "10%", now.AddHours(1))
			},
			SourceTrust.Official,
			SnapshotStatus.Ready,
			now,
			ProviderAccountIdentity:
				viewModel.Profile.ProviderAccountIdentity);

		viewModel.ApplySnapshot(snapshot);

		Assert.Equal(
			new[] { "future", "past" },
			viewModel.UsageMetrics.Select(metric => metric.Key));
	}

	[Fact]
	public void MarkRefreshing_NotifiesAllBindableStatusProperties()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Claude) with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		viewModel.MarkRefreshing();

		Assert.Contains(nameof(AccountUsageViewModel.StatusKind), changedProperties);
		Assert.Contains(nameof(AccountUsageViewModel.StatusSeverity), changedProperties);
		Assert.Contains(nameof(AccountUsageViewModel.StatusText), changedProperties);
		Assert.Contains(nameof(AccountUsageViewModel.DisplayStatusSeverity), changedProperties);
		Assert.Contains(nameof(AccountUsageViewModel.DisplayStatusText), changedProperties);
	}

	[Fact]
	public void MarkRefreshing_WithExistingSnapshot_PreservesLastKnownStatus()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com"),
			canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			viewModel.Profile,
			new[]
			{
				new UsageMetric(
					"session",
					"5 小時用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity:
				viewModel.Profile.ProviderAccountIdentity));

		viewModel.MarkRefreshing();

		Assert.Equal(AccountStatusKind.Ready, viewModel.StatusKind);
		Assert.Equal("可用", viewModel.StatusText);
		Assert.Single(viewModel.UsageMetrics);
	}

	[Fact]
	public void BeginAndAbortProviderAccountChange_WithVerifiedAgyEmail_PreservesIdentityContinuously()
	{
		DateTimeOffset observedAt = new(
			2026,
			8,
			13,
			12,
			0,
			0,
			TimeSpan.Zero);
		AccountProfile profile = new(
			Guid.NewGuid(),
			ProviderKind.Antigravity,
			DisplayName: string.Empty,
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			new[]
			{
				new UsageMetric(
					"agy.gemini.weekly",
					"Gemini 每週",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			observedAt,
			observedAt,
			ProviderAccountIdentity:
				AntigravityOfficialPrintUsageClient.LocalSessionIdentity));
		Assert.True(viewModel.TrySetAntigravityReportedAccountEmail(
			"person@example.com",
			observedAt,
			TimeSpan.FromMinutes(1),
			isStale: false));

		viewModel.BeginProviderAccountChange();
		viewModel.AbortProviderAccountChange();

		Assert.Equal("person@example.com", viewModel.AccountDisplayText);
	}

	[Fact]
	public void BeginProviderAccountChange_ProjectsBusyStateAndDisablesConflictingActions()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com") with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);
		ApplyReadySnapshot(viewModel);
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		viewModel.BeginProviderAccountChange();

		Assert.True(viewModel.IsProviderAccountChangeInProgress);
		Assert.Contains(nameof(AccountUsageViewModel.AccountHeaderSuffixText), changedProperties);
		Assert.False(viewModel.CanChangeAccountSettings);
		Assert.False(viewModel.CanConnectProviderAccount);
		Assert.False(viewModel.CanConnectProviderAccountAfterSave);
		Assert.True(viewModel.CanCancelProviderAccountConnection);
		Assert.True(viewModel.CanInvokeProviderAccountAction);
		Assert.False(viewModel.CanOpenMainAccountMenu);
		Assert.False(viewModel.CanRefreshUsage);
		Assert.Equal(
			"取消 Claude 帳號連接",
			viewModel.ClaudeAccountActionText);
		Assert.Equal(AccountStatusKind.Ready, viewModel.StatusKind);
		Assert.Equal(AccountStatusSeverity.Positive, viewModel.StatusSeverity);
		Assert.Equal("可用", viewModel.StatusText);
		Assert.Equal(
			AccountStatusSeverity.Informational,
			viewModel.DisplayStatusSeverity);
		Assert.Equal("連接中", viewModel.DisplayStatusText);
		Assert.Equal("連接中", viewModel.AccountDisplayText);
		Assert.Contains(
			nameof(AccountUsageViewModel.IsProviderAccountChangeInProgress),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanChangeAccountSettings),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanConnectProviderAccount),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanCancelProviderAccountConnection),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanInvokeProviderAccountAction),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanRefreshUsage),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.DisplayStatusSeverity),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.DisplayStatusText),
			changedProperties);
	}

	[Fact]
	public void EndProviderAccountChange_RestoresStatusAndActions()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com") with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);
		ApplyReadySnapshot(viewModel);
		viewModel.BeginProviderAccountChange();
		List<string?> changedProperties = new();
		viewModel.PropertyChanged += (_, eventArgs) =>
			changedProperties.Add(eventArgs.PropertyName);

		viewModel.EndProviderAccountChange();

		Assert.False(viewModel.IsProviderAccountChangeInProgress);
		Assert.Contains(nameof(AccountUsageViewModel.AccountHeaderSuffixText), changedProperties);
		Assert.True(viewModel.CanChangeAccountSettings);
		Assert.True(viewModel.CanConnectProviderAccount);
		Assert.False(viewModel.CanCancelProviderAccountConnection);
		Assert.True(viewModel.CanInvokeProviderAccountAction);
		Assert.True(viewModel.CanRefreshUsage);
		Assert.Equal("連接 Claude 帳號", viewModel.ClaudeAccountActionText);
		Assert.Equal(AccountStatusKind.Ready, viewModel.StatusKind);
		Assert.Equal(AccountStatusSeverity.Positive, viewModel.StatusSeverity);
		Assert.Equal("可用", viewModel.StatusText);
		Assert.Equal(AccountStatusSeverity.Positive, viewModel.DisplayStatusSeverity);
		Assert.Equal("可用", viewModel.DisplayStatusText);
		Assert.Contains(
			nameof(AccountUsageViewModel.IsProviderAccountChangeInProgress),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanChangeAccountSettings),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanConnectProviderAccount),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanCancelProviderAccountConnection),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanInvokeProviderAccountAction),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.CanRefreshUsage),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.DisplayStatusSeverity),
			changedProperties);
		Assert.Contains(
			nameof(AccountUsageViewModel.DisplayStatusText),
			changedProperties);
	}

	[Fact]
	public void AbortProviderAccountChange_RestoresPreviousIdentity()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com") with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);
		ApplyReadySnapshot(viewModel);
		viewModel.BeginProviderAccountChange();
		viewModel.SeedProviderAccountIdentity("replacement@example.com");

		viewModel.AbortProviderAccountChange();

		Assert.False(viewModel.IsProviderAccountChangeInProgress);
		Assert.Equal("person@example.com", viewModel.ProviderAccountIdentity);
		Assert.Equal(
			"person@example.com",
			viewModel.AccountDisplayText);
		Assert.Equal("切換 Claude 帳號", viewModel.ClaudeAccountActionText);
	}

	[Fact]
	public void AccountStatusAnnouncementPolicy_WithReadyAccountWithoutNotice_Skips()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(
				ProviderKind.Claude,
				providerAccountIdentity: "person@example.com"),
			canManage: true);
		ApplyReadySnapshot(viewModel);

		string? announcement =
			AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel);

		Assert.Null(announcement);
	}

	[Fact]
	public void AccountStatusAnnouncementPolicy_WithRecoveryAction_IncludesNextStep()
	{
		AccountUsageViewModel viewModel = new(
			CreateProfile(ProviderKind.Claude) with
			{
				HasAcceptedClaudeQuotaRisk = true
			},
			canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			viewModel.Profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Error,
			DateTimeOffset.UtcNow,
			Error: "登入已失效。",
			RecoveryAction: UsageRecoveryAction.ConnectAccount));

		string? announcement =
			AccountStatusAnnouncementPolicy.CreateAnnouncement(viewModel);

		Assert.NotNull(announcement);
		Assert.Contains("測試帳號", announcement, StringComparison.Ordinal);
		Assert.Contains("讀取失敗", announcement, StringComparison.Ordinal);
		Assert.Contains("連接後即可讀取用量", announcement, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(nameof(AccountUsageViewModel.DisplayStatusText))]
	[InlineData(nameof(AccountUsageViewModel.NoticeText))]
	[InlineData(nameof(AccountUsageViewModel.RecoveryActionDescription))]
	[InlineData(nameof(AccountUsageViewModel.SecondaryText))]
	[InlineData("")]
	public void AccountStatusAnnouncementPolicy_WithRelevantProperty_Queues(
		string propertyName)
	{
		Assert.True(
			AccountStatusAnnouncementPolicy.IsAnnouncementProperty(propertyName));
	}

	[Fact]
	public void AccountStatusAnnouncementPolicy_WithUnrelatedProperty_Skips()
	{
		Assert.False(
			AccountStatusAnnouncementPolicy.IsAnnouncementProperty(
				nameof(AccountUsageViewModel.CanMoveUp)));
	}

	private static AccountUsageViewModel CreateClaudeContextViewModel(
		string publicBindingIdentity,
		SubscriptionVerificationState verificationState,
		string? subscriptionScopeDisplayName = null)
	{
		AccountProfile profile = CreateProfile(
			ProviderKind.Claude,
			providerAccountIdentity: publicBindingIdentity);
		AccountUsageViewModel viewModel = new(profile, canManage: true);
		viewModel.ApplySnapshot(new UsageSnapshot(
			profile,
			Array.Empty<UsageMetric>(),
			SourceTrust.OfficialExperimental,
			verificationState == SubscriptionVerificationState.TransientProbeError
				? SnapshotStatus.Stale
				: SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: publicBindingIdentity,
			ProviderAccountDisplayIdentity: "person@example.com",
			SubscriptionScopeDisplayName: subscriptionScopeDisplayName,
			PlanTier: "max",
			SubscriptionVerificationState: verificationState));
		return viewModel;
	}

	private static void ApplyReadySnapshot(AccountUsageViewModel viewModel)
	{
		string providerAccountIdentity =
			viewModel.Profile.ProviderAccountIdentity ??
			throw new InvalidOperationException(
				"Ready snapshot tests require a durable provider identity.");
		viewModel.ApplySnapshot(new UsageSnapshot(
			viewModel.Profile,
			new[]
			{
				new UsageMetric(
					"session",
					"5 小時用量",
					25,
					"已使用 25%")
			},
			SourceTrust.OfficialExperimental,
			SnapshotStatus.Ready,
			DateTimeOffset.UtcNow,
			ProviderAccountIdentity: providerAccountIdentity));
	}

	private static AccountProfile CreateProfile(
		ProviderKind provider,
		bool isEnabled = true,
		string? providerAccountIdentity = null)
	{
		return new AccountProfile(
			Guid.NewGuid(),
			provider,
			"測試帳號",
			IsEnabled: isEnabled,
			ProviderAccountIdentity: providerAccountIdentity);
	}
}
