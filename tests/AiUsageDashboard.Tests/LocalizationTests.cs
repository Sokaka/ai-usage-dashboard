using System.Globalization;
using System.Text;

using AiUsageDashboard.Core.Localization;

namespace AiUsageDashboard.Tests;

public sealed class LocalizationTests
{
	[Fact]
	public void LanguageScope_RestoresNestedLanguageAndPreservesProtocolCulture()
	{
		CultureInfo originalCulture = CultureInfo.CurrentCulture;
		using (UiText.UseLanguage(AppLanguage.English))
		{
			Assert.Equal("en", UiText.Culture.Name);
			Assert.Equal("Show widget", UiText.Get("Shell.App081"));
			using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
			{
				Assert.Equal("zh-TW", UiText.Culture.Name);
				Assert.Equal("顯示浮窗", UiText.Get("Shell.App081"));
			}

			Assert.Equal(AppLanguage.English, UiText.CurrentLanguage);
			Assert.Equal("Show widget", UiText.Get("Shell.App081"));
		}

		Assert.Same(originalCulture, CultureInfo.CurrentCulture);
	}

	[Fact]
	public async Task LanguageScopes_IsolateConcurrentAsyncOperations()
	{
		var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int startedCount = 0;
		async Task<string> ReadTextAsync(AppLanguage language)
		{
			using var languageScope = UiText.UseLanguage(language);
			if (Interlocked.Increment(ref startedCount) == 2)
			{
				bothStarted.SetResult();
			}

			await bothStarted.Task;
			return UiText.Get("Shell.App081");
		}

		Task<string> english = ReadTextAsync(AppLanguage.English);
		Task<string> chinese = ReadTextAsync(AppLanguage.TraditionalChinese);
		Assert.Equal("Show widget", await english);
		Assert.Equal("顯示浮窗", await chinese);
	}

	[Fact]
	public void ResourceCatalogs_ProvideTheSameKeysAndValidFormatsInBothLanguages()
	{
		IReadOnlyDictionary<string, string> english;
		IReadOnlyDictionary<string, string> chinese;
		using (UiText.UseLanguage(AppLanguage.English))
		{
			english = UiText.GetResources();
		}

		using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
		{
			chinese = UiText.GetResources();
		}

		Assert.Equal(english.Keys.Order(), chinese.Keys.Order());
		foreach (string key in english.Keys)
		{
			Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
			Assert.False(string.IsNullOrWhiteSpace(chinese[key]), key);
			CompositeFormat englishFormat = CompositeFormat.Parse(english[key]);
			CompositeFormat chineseFormat = CompositeFormat.Parse(chinese[key]);
			Assert.Equal(englishFormat.MinimumArgumentCount, chineseFormat.MinimumArgumentCount);
		}
	}

	[Theory]
	[InlineData(AppLanguage.English)]
	[InlineData(AppLanguage.TraditionalChinese)]
	public void Resources_UseStableLineEndingsAcrossLookupAndCatalog(AppLanguage language)
	{
		using var languageScope = UiText.UseLanguage(language);
		IReadOnlyDictionary<string, string> resources = UiText.GetResources();
		Assert.Contains(resources.Values, value => value.Contains('\n'));
		foreach ((string key, string value) in resources)
		{
			Assert.DoesNotContain('\r', value);
			Assert.Equal(value, UiText.Get(key));
		}
	}

	[Theory]
	[InlineData(AppLanguage.English, "The card's existing connection is unaffected.")]
	[InlineData(AppLanguage.TraditionalChinese, "原本的卡片連接不受影響。")]
	public void ResourceLineEndings_PreserveOpaqueArgumentAndDiagnosticBytes(
		AppLanguage language,
		string expectedGuidance)
	{
		using var languageScope = UiText.UseLanguage(language);
		const string Diagnostic = "Opaque provider diagnostic\r\n私有原文 {0}\rEnd";
		string expected = $"{Diagnostic}\n\n{expectedGuidance}";
		Assert.Equal(expected, UiText.Format(
			"Status.TheCardSExistingConnectionIsUnaffected", Diagnostic));
		Assert.Equal(expected, UiText.Translate($"{Diagnostic}\n\n原本的卡片連接不受影響。"));
		Assert.Equal(Diagnostic, UiText.Translate(Diagnostic));
		Assert.Equal(Diagnostic, UiText.Translate(Diagnostic));
	}

	[Theory]
	[InlineData((int)AppLanguage.English, "Version 2.0 is available. Open AI Usage to see update options.")]
	[InlineData((int)AppLanguage.TraditionalChinese, "版本 2.0 已可使用。開啟 AI Usage 查看更新選項。")]
	public void FormattedText_PreservesTheSuppliedVersion(int language, string expected)
	{
		using var languageScope = UiText.UseLanguage((AppLanguage)language);
		Assert.Equal(expected, UiText.Format("Shell.App028", "2.0"));
	}

	[Fact]
	public void UnknownLanguageAndMissingResources_FailWithActionableContext()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => UiText.UseLanguage((AppLanguage)99));
		Assert.Throws<ArgumentOutOfRangeException>(() => UiText.SetLanguage((AppLanguage)99));
		Assert.Contains("Unknown", Assert.Throws<ArgumentException>(
			() => UiText.Get("Unknown.Widget")).Message, StringComparison.Ordinal);
		Assert.Contains("Shell.DoesNotExist", Assert.Throws<InvalidOperationException>(
			() => UiText.Get("Shell.DoesNotExist")).Message, StringComparison.Ordinal);
	}
}
