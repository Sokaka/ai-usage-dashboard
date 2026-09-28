using AiUsageDashboard.Core.Localization;

namespace AiUsageDashboard.Tests;

public sealed class ProviderLocalizationTests
{
	[Theory]
	[InlineData("Claude 登入逾時，請再試一次。", "Claude", "timed out", "Try again")]
	[InlineData("Codex 登入已失效；請重新連接此帳號。", "Codex", "expired", "Reconnect")]
	[InlineData("Copilot 訂閱資訊查詢逾時；用量仍可使用。", "Copilot", "timed out", "still available")]
	[InlineData("Grok 帳號的連接資料已損壞，請重新連接。", "Grok", "corrupt", "Reconnect")]
	[InlineData("找不到 Antigravity CLI 或執行所需檔案。請確認 Antigravity 已安裝且可正常啟動；既有登入不受影響。", "Antigravity", "not found", "existing sign-in is unaffected")]
	public void ProviderRecovery_TranslatesTheFailureAndRequiredAction(
		string chinese,
		string provider,
		string failure,
		string action)
	{
		AssertBilingual(chinese, english =>
		{
			Assert.Contains(provider, english, StringComparison.Ordinal);
			Assert.Contains(failure, english, StringComparison.Ordinal);
			Assert.Contains(action, english, StringComparison.Ordinal);
		});
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void AntigravitySafetyLatch_TranslatesTheReasonAndPreservesTheRecoveryRoute(
		bool useOfficialPrint)
	{
		string nextStep = useOfficialPrint
			? "請按「重新檢查 Antigravity 用量」再試一次。"
			: "請重新連接 Antigravity 帳號。";
		string chinese =
			$"Antigravity 用量檢查已暫停：上次檢查逾時。AI Usage 不會自動再試。{nextStep}";

		AssertBilingual(chinese, english =>
		{
			Assert.Contains("paused", english, StringComparison.Ordinal);
			Assert.Contains("timed out", english, StringComparison.Ordinal);
			Assert.Contains("will not retry automatically", english, StringComparison.Ordinal);
			Assert.Contains(
				useOfficialPrint ? "Check Antigravity usage again" : "Reconnect your Antigravity account",
				english,
				StringComparison.Ordinal);
		});
	}

	[Fact]
	public void ClaudeAutomaticSafetyRecovery_TranslatesTheReasonAndKeepsTheRetryPolicy()
	{
		string chinese =
			"Claude Code 2.1.170 回傳的用量格式暫時無法辨識。" +
			"AI Usage 稍後會自動再試，並保留上次成功讀取的資料。";

		AssertBilingual(chinese, english =>
		{
			Assert.Contains("2.1.170", english, StringComparison.Ordinal);
			Assert.Contains("cannot be recognized", english, StringComparison.Ordinal);
			Assert.Contains("will retry automatically", english, StringComparison.Ordinal);
			Assert.Contains("keep the last successfully read data", english, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void ClaudeManualSafetyRecovery_StillExplainsPossibleUsageAndRequiresExplicitRetry()
	{
		string chinese =
			"Claude Code 2.1.170 的 `/usage` 可能產生用量，或結果需要人工確認。" +
			"AI Usage 不會自動再試。請按「重新檢查 Claude 用量」再試一次。";

		AssertBilingual(chinese, english =>
		{
			Assert.Contains("2.1.170", english, StringComparison.Ordinal);
			Assert.Contains("may have consumed usage", english, StringComparison.Ordinal);
			Assert.Contains("manual verification", english, StringComparison.Ordinal);
			Assert.Contains("will not retry automatically", english, StringComparison.Ordinal);
			Assert.Contains("Check Claude usage again", english, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void CodexBucketUsageLabel_TranslatesTheWindowAndPreservesTheSourceBucketName()
	{
		const string bucketName = "研究團隊";
		string chinese = $"{bucketName} · 7 天用量";

		using (UiText.UseLanguage(AppLanguage.English))
		{
			Assert.Equal($"{bucketName} · 7-day usage", UiText.Translate(chinese));
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			Assert.Equal(chinese, UiText.Translate($"{bucketName} · 7-day usage"));
		}
	}

	[Fact]
	public void CopilotSharedQuota_TranslatesTheLimitExplanationAndExtraUsageTogether()
	{
		const string chinese = "已使用 1,234；上限由共用額度與預算控制，額外用量已開啟";

		AssertBilingual(chinese, english =>
		{
			Assert.Contains("1,234", english, StringComparison.Ordinal);
			Assert.Contains("shared quota", english, StringComparison.Ordinal);
			Assert.Contains("budget", english, StringComparison.Ordinal);
			Assert.Contains("extra usage enabled", english, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void CopilotUnlimitedQuota_TranslatesBothTheLimitAndContinuedUsageNotice()
	{
		const string chinese = "已使用 42 次（無上限），額度用完後仍可使用";

		AssertBilingual(chinese, english =>
		{
			Assert.Contains("42 requests", english, StringComparison.Ordinal);
			Assert.Contains("unlimited", english, StringComparison.Ordinal);
			Assert.Contains("usage remains available after the quota is exhausted", english, StringComparison.Ordinal);
		});
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CliVersionDiagnostic_TranslatesKnownAndUnknownVersionsWithoutChangingTheValues(
		bool hasDetectedVersion)
	{
		string retryMessage = hasDetectedVersion
			? "暫時無法讀取 Codex 用量，稍後會自動再試。"
			: "暫時無法讀取 Grok 用量，稍後會自動再試。";
		string detectedVersion = hasDetectedVersion
			? "偵測到 Codex CLI 0.145.1"
			: "無法辨識 Grok Build CLI 版號";
		string referenceVersion = hasDetectedVersion ? "0.144.1" : "1.0.3";
		string chinese = $"{retryMessage} CLI 版本診斷：{detectedVersion}；本版相容性基準為 {referenceVersion}。這個版本尚未驗證，但不代表不支援；版本差異可能是原因之一。";

		AssertBilingual(chinese, english =>
		{
			Assert.Contains(hasDetectedVersion ? "Codex CLI 0.145.1" : "Grok Build CLI", english, StringComparison.Ordinal);
			Assert.Contains(referenceVersion, english, StringComparison.Ordinal);
			Assert.Contains("will retry automatically", english, StringComparison.Ordinal);
			Assert.Contains("does not mean it is unsupported", english, StringComparison.Ordinal);
			if (!hasDetectedVersion)
			{
				Assert.Contains("could not be recognized", english, StringComparison.Ordinal);
			}
		});
	}

	[Fact]
	public void OpaqueProviderArgumentsAndUnknownRawDiagnostics_AreKeptInBothLanguages()
	{
		const string path = @"C:\Synthetic\研究團隊\copilot.exe";
		const string fieldName = "組織顯示名稱";
		const string rawDiagnostic = "CLI remote text: 尚未連接 Grok 帳號。 [raw_834]";
		string chinesePath = $"Copilot CLI 候選必須是絕對 EXE 路徑：{path}";
		string englishPath = $"The Copilot CLI candidate must use an absolute EXE path: {path}";
		string chineseField = $"Codex {fieldName} 必須是 string 或 null。";
		string englishField = $"Codex {fieldName} must be a string or null.";

		using (UiText.UseLanguage(AppLanguage.English))
		{
			Assert.Equal(englishPath, UiText.Translate(chinesePath));
			Assert.Equal(englishField, UiText.Translate(chineseField));
			Assert.Equal(rawDiagnostic, UiText.Translate(rawDiagnostic));
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			Assert.Equal(chinesePath, UiText.Translate(englishPath));
			Assert.Equal(chineseField, UiText.Translate(englishField));
			Assert.Equal(rawDiagnostic, UiText.Translate(rawDiagnostic));
		}
	}

	private static void AssertBilingual(string chinese, Action<string> verifyEnglish)
	{
		string english;
		using (UiText.UseLanguage(AppLanguage.English))
		{
			english = UiText.Translate(chinese);
			Assert.NotEqual(chinese, english);
			Assert.DoesNotMatch(@"[\p{IsCJKUnifiedIdeographs}]", english);
			verifyEnglish(english);
		}
		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			Assert.Equal(chinese, UiText.Translate(english));
			Assert.Equal(chinese, UiText.Translate(chinese));
		}
	}
}
