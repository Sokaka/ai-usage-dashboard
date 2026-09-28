using System.Reflection;

using AiUsageDashboard.Core.Localization;

using Xunit.Sdk;

namespace AiUsageDashboard.Tests;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class LegacyChineseUiTestAttribute : BeforeAfterTestAttribute
{
	private readonly AsyncLocal<IDisposable?> _scope = new();

	public override void Before(MethodInfo methodUnderTest)
	{
		_scope.Value = UiText.UseLanguage(AppLanguage.TraditionalChinese);
	}

	public override void After(MethodInfo methodUnderTest)
	{
		_scope.Value?.Dispose();
		_scope.Value = null;
	}
}
