using AiUsageDashboard.Core.Localization;

namespace AiUsageDashboard.Tests;

[LegacyChineseUiTest]
public sealed class LegacyChineseUiTestAttributeTests
{
	[Fact]
	public async Task ClassAttributeScopeFlowsAcrossAwaitAndRestoresNestedLanguage()
	{
		Assert.Equal(AppLanguage.TraditionalChinese, UiText.CurrentLanguage);
		await Task.Yield();
		Assert.Equal(AppLanguage.TraditionalChinese, UiText.CurrentLanguage);
		using (UiText.UseLanguage(AppLanguage.English))
		{
			await Task.Yield();
			Assert.Equal(AppLanguage.English, UiText.CurrentLanguage);
		}
		Assert.Equal(AppLanguage.TraditionalChinese, UiText.CurrentLanguage);
	}
}
