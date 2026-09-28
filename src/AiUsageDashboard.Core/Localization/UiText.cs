using System.Collections;
using System.Globalization;
using System.Resources;

namespace AiUsageDashboard.Core.Localization;

public static partial class UiText
{
	private sealed class LanguageScope : IDisposable
	{
		private readonly AppLanguage? _previousLanguage;
		private bool _isDisposed;

		public LanguageScope(AppLanguage language)
		{
			_previousLanguage = _scopedLanguage.Value;
			_scopedLanguage.Value = language;
		}

		public void Dispose()
		{
			if (_isDisposed)
			{
				return;
			}

			_scopedLanguage.Value = _previousLanguage;
			_isDisposed = true;
		}
	}

	public static event EventHandler? LanguageChanged;

	private static readonly AsyncLocal<AppLanguage?> _scopedLanguage = new();
	private static readonly IReadOnlyDictionary<string, ResourceManager> _resourceManagers =
		new Dictionary<string, ResourceManager>(StringComparer.Ordinal)
		{
			["Shell"] = CreateResourceManager("Shell"),
			["Windows"] = CreateResourceManager("Windows"),
			["Status"] = CreateResourceManager("Status"),
			["Persistence"] = CreateResourceManager("Persistence"),
			["Provider"] = CreateResourceManager("Provider")
		};
	private static int _currentLanguage = (int)AppLanguage.English;

	public static AppLanguage CurrentLanguage =>
		_scopedLanguage.Value ?? (AppLanguage)Volatile.Read(ref _currentLanguage);

	public static CultureInfo Culture => GetCulture(CurrentLanguage);

	public static string Get(string key)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		var separatorIndex = key.IndexOf('.');
		if ((separatorIndex <= 0) || (separatorIndex == key.Length - 1)
			|| !_resourceManagers.TryGetValue(key[..separatorIndex], out var resourceManager))
		{
			throw new ArgumentException($"Unknown UI resource key '{key}'.", nameof(key));
		}

		return NormalizeResourceLineEndings(
			resourceManager.GetString(key[(separatorIndex + 1)..], Culture)
				?? throw new InvalidOperationException(
					$"UI resource '{key}' is missing for language '{CurrentLanguage}'."));
	}

	public static string Format(string key, params object?[] arguments)
	{
		return string.Format(Culture, Get(key), arguments);
	}

	public static partial string Translate(string text);

	public static IReadOnlyDictionary<string, string> GetResources()
	{
		var resources = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (domain, resourceManager) in _resourceManagers)
		{
			var resourceSet = resourceManager.GetResourceSet(Culture, true, true)
				?? throw new InvalidOperationException(
					$"UI resource catalog '{domain}' is missing for language '{CurrentLanguage}'.");
			foreach (DictionaryEntry resource in resourceSet)
			{
				if ((resource.Key is string name) && (resource.Value is string value))
				{
					resources.Add($"{domain}.{name}", NormalizeResourceLineEndings(value));
				}
			}
		}

		return resources;
	}

	public static void SetLanguage(AppLanguage language)
	{
		ValidateLanguage(language);
		if (Interlocked.Exchange(ref _currentLanguage, (int)language) != (int)language)
		{
			LanguageChanged?.Invoke(null, EventArgs.Empty);
		}
	}

	public static IDisposable UseLanguage(AppLanguage language)
	{
		ValidateLanguage(language);
		return new LanguageScope(language);
	}

	private static CultureInfo GetCulture(AppLanguage language)
	{
		return language switch
		{
			AppLanguage.English => CultureInfo.GetCultureInfo("en"),
			AppLanguage.TraditionalChinese => CultureInfo.GetCultureInfo("zh-TW"),
			_ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported App language.")
		};
	}

	private static ResourceManager CreateResourceManager(string domain)
	{
		return new ResourceManager(
			$"AiUsageDashboard.Core.Localization.Resources.{domain}", typeof(UiText).Assembly);
	}

	private static string NormalizeResourceLineEndings(string value)
	{
		// 只正規化資源換行，使用者內容與原始診斷保留原文。
		return value.Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	private static void ValidateLanguage(AppLanguage language)
	{
		if (!Enum.IsDefined(language))
		{
			throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported App language.");
		}
	}
}
