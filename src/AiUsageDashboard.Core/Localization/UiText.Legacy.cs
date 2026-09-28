using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiUsageDashboard.Core.Localization;

public static partial class UiText
{
	private sealed record LegacyTemplate(
		Regex Pattern,
		Regex? CompositionPattern,
		string Translation,
		int[] ArgumentIndexes,
		string Prefix,
		string RequiredLiteral,
		bool HasPresentationArguments,
		int LiteralLength);

	private sealed record LegacyCatalog(
		IReadOnlyDictionary<string, string> Exact,
		IReadOnlyList<LegacyTemplate> Templates)
	{
		internal object CacheLock { get; } = new();
		internal Dictionary<string, string> Cache { get; } = new(StringComparer.Ordinal);
		internal Queue<string> CacheOrder { get; } = new();
	}

	private const int MaximumLegacyCacheEntries = 1024;
	private const int MaximumLegacyInputLength = 4096;

	private static readonly Lazy<LegacyCatalog> _englishLegacyCatalog =
		new(() => CreateLegacyCatalog("zh-TW", "en"));
	private static readonly Lazy<LegacyCatalog> _chineseLegacyCatalog =
		new(() => CreateLegacyCatalog("en", "zh-TW"));
	private static readonly Regex _legacyPlaceholder = new(
		@"(?<!\{)\{(?<index>[0-9]+)(?:,-?[0-9]+)?(?::[^{}]+)?\}(?!\})",
		RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
		TimeSpan.FromMilliseconds(100));

	public static partial string Translate(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if ((text.Length == 0) || (text.Length > MaximumLegacyInputLength))
		{
			return text;
		}

		LegacyCatalog catalog = CurrentLanguage == AppLanguage.English
			? _englishLegacyCatalog.Value
			: _chineseLegacyCatalog.Value;
		if (catalog.Exact.TryGetValue(text, out string? translation))
		{
			return translation;
		}
		lock (catalog.CacheLock)
		{
			if (catalog.Cache.TryGetValue(text, out string? cached))
			{
				return cached;
			}
		}

		try
		{
			translation = TranslateLegacyTemplate(catalog, text)
				?? TranslateKnownComposition(catalog, text)
				?? text;
		}
		catch (RegexMatchTimeoutException)
		{
			// 語系比對無法安全完成時保留原文，避免來源診斷阻斷原有操作。
			translation = text;
		}

		return CacheLegacyTranslation(catalog, text, translation);
	}

	private static string? TranslateLegacyTemplate(LegacyCatalog catalog, string text)
	{
		foreach (LegacyTemplate template in catalog.Templates)
		{
			if (!CanMatchLegacyTemplate(template, text))
			{
				continue;
			}
			Match match = template.Pattern.Match(text);
			if (!match.Success)
			{
				continue;
			}

			object?[] arguments = new object?[template.ArgumentIndexes.Max() + 1];
			for (int index = 0; index < template.ArgumentIndexes.Length; index++)
			{
				string argument = match.Groups[$"a{index}"].Value;
				arguments[template.ArgumentIndexes[index]] = template.HasPresentationArguments
					? Translate(argument)
					: argument;
			}

			// 只翻譯已知完整句型；帳號、路徑與來源診斷等參數保留原文。
			return string.Format(Culture, template.Translation, arguments);
		}

		return null;
	}

	private static bool CanMatchLegacyTemplate(LegacyTemplate template, string text)
	{
		return (text.StartsWith(template.Prefix, StringComparison.Ordinal))
			&& (text.Contains(template.RequiredLiteral, StringComparison.Ordinal));
	}

	private static string? TranslateKnownComposition(LegacyCatalog catalog, string text)
	{
		StringBuilder translated = new();
		int offset = 0;
		while (offset < text.Length)
		{
			string remaining = text[offset..];
			KeyValuePair<string, string> exact = catalog.Exact
				.Where(pair => IsCompositionFragment(pair.Key) &&
					remaining.StartsWith(pair.Key, StringComparison.Ordinal))
				.OrderByDescending(pair => pair.Key.Length)
				.FirstOrDefault();
			if (exact.Key is not null)
			{
				translated.Append(exact.Value);
				offset += exact.Key.Length;
				continue;
			}

			LegacyTemplate? matchedTemplate = null;
			Match? matchedFragment = null;
			foreach (LegacyTemplate template in catalog.Templates)
			{
				if ((template.CompositionPattern is null) ||
					!CanMatchLegacyTemplate(template, remaining))
				{
					continue;
				}
				Match match = template.CompositionPattern.Match(remaining);
				if (match.Success)
				{
					matchedTemplate = template;
					matchedFragment = match;
					break;
				}
			}
			if ((matchedTemplate is not null) && (matchedFragment is not null))
			{
				object?[] arguments = new object?[matchedTemplate.ArgumentIndexes.Max() + 1];
				for (int index = 0; index < matchedTemplate.ArgumentIndexes.Length; index++)
				{
					string argument = matchedFragment.Groups[$"a{index}"].Value;
					arguments[matchedTemplate.ArgumentIndexes[index]] = matchedTemplate.HasPresentationArguments
						? Translate(argument)
						: argument;
				}
				translated.Append(string.Format(Culture, matchedTemplate.Translation, arguments));
				offset += matchedFragment.Length;
				continue;
			}
			if (char.IsWhiteSpace(text[offset]))
			{
				translated.Append(text[offset++]);
				continue;
			}
			// 複合提示必須全部由已知句子組成，否則整段保留來源診斷。
			return null;
		}
		return translated.ToString();
	}

	private static bool IsCompositionFragment(string text)
	{
		return text.EndsWith('。') || text.EndsWith('.') || text.EndsWith('\n');
	}

	private static string CacheLegacyTranslation(
		LegacyCatalog catalog,
		string source,
		string translation)
	{
		lock (catalog.CacheLock)
		{
			if (catalog.Cache.TryAdd(source, translation))
			{
				catalog.CacheOrder.Enqueue(source);
				if (catalog.Cache.Count > MaximumLegacyCacheEntries)
				{
					catalog.Cache.Remove(catalog.CacheOrder.Dequeue());
				}
			}
		}
		return translation;
	}

	private static LegacyCatalog CreateLegacyCatalog(
		string sourceCultureName,
		string targetCultureName)
	{
		CultureInfo sourceCulture = CultureInfo.GetCultureInfo(sourceCultureName);
		CultureInfo targetCulture = CultureInfo.GetCultureInfo(targetCultureName);
		Dictionary<string, string> exact = new(StringComparer.Ordinal);
		List<LegacyTemplate> templates = new();
		HashSet<string> seenTemplates = new(StringComparer.Ordinal);
		foreach (System.Resources.ResourceManager manager in _resourceManagers.Values)
		{
			System.Resources.ResourceSet? resources =
				manager.GetResourceSet(sourceCulture, true, true);
			if (resources is null)
			{
				throw new InvalidOperationException(
					$"Localization catalog {manager.BaseName} is unavailable for {sourceCultureName}.");
			}

			foreach (System.Collections.DictionaryEntry entry in resources)
			{
				if ((entry.Key is not string key) ||
					(entry.Value is not string source) ||
					(manager.GetString(key, targetCulture) is not string target) ||
					(source == target))
				{
					continue;
				}

				MatchCollection placeholders = _legacyPlaceholder.Matches(source);
				if (placeholders.Count == 0)
				{
					exact.TryAdd(source, target);
					continue;
				}

				if (!seenTemplates.Add(source))
				{
					continue;
				}

				StringBuilder pattern = new(@"\A");
				int offset = 0;
				int literalLength = 0;
				string requiredLiteral = string.Empty;
				List<int> argumentIndexes = new();
				foreach (Match placeholder in placeholders)
				{
					string literal = source[offset..placeholder.Index];
					literalLength += literal.Length;
					if (literal.Length > requiredLiteral.Length)
					{
						requiredLiteral = literal;
					}
					pattern.Append(Regex.Escape(literal));
					pattern.Append($"(?<a{argumentIndexes.Count}>.*?)");
					argumentIndexes.Add(int.Parse(
						placeholder.Groups["index"].Value,
						CultureInfo.InvariantCulture));
					offset = placeholder.Index + placeholder.Length;
				}

				string suffix = source[offset..];
				literalLength += suffix.Length;
				if (suffix.Length > requiredLiteral.Length)
				{
					requiredLiteral = suffix;
				}
				pattern.Append(Regex.Escape(suffix));
				pattern.Append(@"\z");
				if (literalLength == 0)
				{
					continue;
				}

				templates.Add(new LegacyTemplate(
					new Regex(
						pattern.ToString(),
						RegexOptions.CultureInvariant |
							RegexOptions.ExplicitCapture |
							RegexOptions.Singleline,
						TimeSpan.FromMilliseconds(100)),
					IsCompositionFragment(source)
						? new Regex(
							pattern.ToString()[..^2],
							RegexOptions.CultureInvariant |
								RegexOptions.ExplicitCapture |
								RegexOptions.Singleline,
							TimeSpan.FromMilliseconds(100))
						: null,
					target,
					argumentIndexes.ToArray(),
					source[..placeholders[0].Index],
					requiredLiteral,
					key is "AIUsageScheduledAnAutomaticRetryIn" or "UpdatesAreTooFrequentTryAgainIn",
					literalLength));
			}
		}

		return new LegacyCatalog(
			exact,
			templates.OrderByDescending(template => template.LiteralLength).ToArray());
	}
}
