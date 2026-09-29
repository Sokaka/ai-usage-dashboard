namespace AiUsageDashboard.Updater.Core;

public enum UpdaterDisplayLanguage
{
	English = 0,
	TraditionalChinese = 1
}

public static class UpdaterDisplayLanguageContract
{
	public const string LanguageEnvironmentVariableName =
		"AI_USAGE_DASHBOARD_UI_LANGUAGE";

	public static bool TryParse(string? value, out UpdaterDisplayLanguage language)
	{
		switch (value)
		{
			case nameof(UpdaterDisplayLanguage.English):
				language = UpdaterDisplayLanguage.English;
				return true;
			case nameof(UpdaterDisplayLanguage.TraditionalChinese):
				language = UpdaterDisplayLanguage.TraditionalChinese;
				return true;
			default:
				language = UpdaterDisplayLanguage.English;
				return false;
		}
	}
}
