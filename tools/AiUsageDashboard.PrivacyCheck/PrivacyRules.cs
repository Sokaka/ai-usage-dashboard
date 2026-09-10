using System.Text;
using System.Text.RegularExpressions;

namespace AiUsageDashboard.PrivacyCheck;

public static class PrivacyRules
{
	private sealed record SecretRule(string Description, Regex Pattern);

	private const int MinimumCredentialDistinctCharacters = 8;
	private const int MaximumMatches = 10000;
	private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(2);
	private static readonly HashSet<string> _placeholderUserNames = new(
		new[]
		{
			"alice", "bob", "default", "example", "placeholder", "public", "runner",
			"sample", "synthetic", "test", "tester", "user", "username"
		},
		StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> _textFileExtensions = new(
		new[]
		{
			".bat", ".cmd", ".config", ".cs", ".csproj", ".json", ".md", ".props",
			".ps1", ".resx", ".sh", ".sln", ".targets", ".toml", ".txt", ".xaml",
			".xml", ".yaml", ".yml"
		},
		StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> _sensitiveFileNames = new(
		new[]
		{
			".env", "accounts.json", "accounts.json.bak", "appsettings.Local.json",
			"auth.json", "credentials.json", "preferences.json", "secrets.json"
		},
		StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> _exampleFileNames = new(
		new[] { ".env.example", "auth.example.json", "credentials.example.json" },
		StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> _sensitiveFileExtensions = new(
		new[] { ".jwk", ".key", ".p12", ".pem", ".pfx", ".pk8" },
		StringComparer.OrdinalIgnoreCase);
	private static readonly string[] _placeholderMarkers =
	{
		"changeme", "dummy", "example", "fake", "not-a-", "placeholder", "redacted",
		"replace", "sample", "secret-value", "synthetic", "test", "token-value", "your-", "xxx"
	};
	private static readonly UTF8Encoding _strictUtf8Encoding = new(false, true);
	private static readonly UnicodeEncoding _strictUtf16LittleEndianEncoding = new(false, false, true);
	private static readonly UnicodeEncoding _strictUtf16BigEndianEncoding = new(true, false, true);
	private static readonly byte[] _utf8ByteOrderMark = { 0xef, 0xbb, 0xbf };
	private static readonly byte[] _utf16LittleEndianByteOrderMark = { 0xff, 0xfe };
	private static readonly byte[] _utf16BigEndianByteOrderMark = { 0xfe, 0xff };
	private static readonly Regex _credentialAssignmentPattern = new(
		@"[""']?(?<name>api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|auth[_-]?token|password|passwd)[""']?\s*(?:=|:)\s*[""']?(?<value>[A-Z0-9_./+\-=:]{24,})",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
		_regexTimeout);
	private static readonly Regex _emailAddressPattern = new(
		@"(?<![A-Z0-9._%+\-])(?<local>[A-Z0-9._%+\-]{1,64})@(?<domain>(?:[A-Z0-9](?:[A-Z0-9\-]{0,61}[A-Z0-9])?\.)+[A-Z]{2,63})(?![A-Z0-9.\-])",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
		_regexTimeout);
	private static readonly Regex _userHomePathPattern = new(
		@"(?:(?:[A-Z]:)?[\\/](?:Users|home)[\\/])(?<user>[^\\/\s""'<>:$%{}]+)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
		_regexTimeout);
	private static readonly SecretRule[] _secretRules =
	{
		new(
			"private-key material",
			new Regex(
				Regex.Escape("-----" + "BEGIN") +
				@"(?: [A-Z0-9]+)? PRIVATE KEY" + Regex.Escape("-----"),
				RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
				_regexTimeout)),
		new(
			"provider API key",
			new Regex(
				@"\bs" + @"k-(?:ant-|proj-)?[A-Z0-9_-]{20,}\b",
				RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
				_regexTimeout)),
		new(
			"GitHub access token",
			new Regex(
				@"\b(?:g" + @"h[pousr]_[A-Z0-9]{30,}|g" + @"ithub_pat_[A-Z0-9_]{40,})\b",
				RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
				_regexTimeout)),
		new(
			"AWS access key",
			new Regex(
				@"\b(?:A" + @"KIA|A" + @"SIA)[A-Z0-9]{16}\b",
				RegexOptions.Compiled | RegexOptions.CultureInvariant,
				_regexTimeout)),
		new(
			"Google API key",
			new Regex(
				@"\bA" + @"Iza[A-Z0-9_-]{35}\b",
				RegexOptions.Compiled | RegexOptions.CultureInvariant,
				_regexTimeout)),
		new(
			"Slack access token",
			new Regex(
				@"\bx" + @"ox[baprs]-[A-Z0-9-]{20,}\b",
				RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
				_regexTimeout)),
		new(
			"credential embedded in URL",
			new Regex(
				@"\bhttps?://[^/\s:@]+:[^@\s/]+@",
				RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
				_regexTimeout))
	};

	public static bool IsSensitiveArtifactName(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		if (_exampleFileNames.Contains(fileName))
		{
			return false;
		}

		if (_sensitiveFileNames.Contains(fileName) ||
			_sensitiveFileExtensions.Contains(Path.GetExtension(fileName)))
		{
			return true;
		}

		return fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
			(fileName.StartsWith("auth.", StringComparison.OrdinalIgnoreCase) &&
				fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ||
			(fileName.StartsWith("credentials.", StringComparison.OrdinalIgnoreCase) &&
				fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ||
			fileName.StartsWith("private-key", StringComparison.OrdinalIgnoreCase) ||
			fileName.StartsWith("accounts.corrupt-", StringComparison.OrdinalIgnoreCase) ||
			fileName.StartsWith("usage-snapshot", StringComparison.OrdinalIgnoreCase) ||
			fileName.StartsWith("agy-profile", StringComparison.OrdinalIgnoreCase) ||
			(fileName.StartsWith("agy-", StringComparison.OrdinalIgnoreCase) &&
				fileName.EndsWith(".profile.json", StringComparison.OrdinalIgnoreCase)) ||
			fileName.StartsWith("antigravity-profile", StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(".private-profile.json", StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(".profile.private.json", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsKnownTextFile(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		return string.Equals(fileName, ".editorconfig", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(fileName, ".gitignore", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(fileName, "LICENSE", StringComparison.OrdinalIgnoreCase) ||
			_exampleFileNames.Contains(fileName) ||
			_textFileExtensions.Contains(Path.GetExtension(fileName));
	}

	public static bool TryDecodeText(byte[] bytes, out string text)
	{
		ArgumentNullException.ThrowIfNull(bytes);
		ReadOnlySpan<byte> payload = bytes;
		Encoding encoding = _strictUtf8Encoding;
		if (payload.StartsWith(_utf8ByteOrderMark))
		{
			payload = payload[_utf8ByteOrderMark.Length..];
		}
		else if (payload.StartsWith(_utf16LittleEndianByteOrderMark))
		{
			encoding = _strictUtf16LittleEndianEncoding;
			payload = payload[_utf16LittleEndianByteOrderMark.Length..];
		}
		else if (payload.StartsWith(_utf16BigEndianByteOrderMark))
		{
			encoding = _strictUtf16BigEndianEncoding;
			payload = payload[_utf16BigEndianByteOrderMark.Length..];
		}
		else if (LooksLikeBomlessUtf16(payload, highByteOffset: 1))
		{
			encoding = _strictUtf16LittleEndianEncoding;
		}
		else if (LooksLikeBomlessUtf16(payload, highByteOffset: 0))
		{
			encoding = _strictUtf16BigEndianEncoding;
		}

		try
		{
			text = encoding.GetString(payload);
		}
		catch (DecoderFallbackException)
		{
			text = string.Empty;
			return false;
		}

		if (text.Any(character => char.IsControl(character) &&
			(character is not '\t' and not '\n' and not '\r' and not '\f')))
		{
			text = string.Empty;
			return false;
		}

		return true;
	}

	public static IReadOnlyList<PrivacyMatch> ScanText(
		string content,
		bool includePersonalData,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(content);
		List<PrivacyMatch> matches = new();
		using StringReader reader = new(content);
		int lineNumber = 0;
		while (reader.ReadLine() is { } line)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lineNumber++;
			if (includePersonalData)
			{
				ScanPersonalData(line, lineNumber, matches);
			}

			if (_credentialAssignmentPattern.Matches(line).Cast<Match>().Any(match =>
				LooksLikeRealCredential(match.Groups["value"].Value)))
			{
				AddMatch(matches, lineNumber, "credential-like assignment");
			}

			foreach (SecretRule rule in _secretRules)
			{
				if (rule.Pattern.IsMatch(line))
				{
					AddMatch(matches, lineNumber, rule.Description);
				}
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		return matches;
	}

	private static void AddMatch(ICollection<PrivacyMatch> matches, int lineNumber, string rule)
	{
		if (matches.Count >= MaximumMatches)
		{
			throw new PrivacyCheckException(
				$"Privacy text scan exceeds {MaximumMatches} matches at line {lineNumber}.");
		}
		matches.Add(new PrivacyMatch(lineNumber, rule));
	}

	private static bool LooksLikeBomlessUtf16(ReadOnlySpan<byte> bytes, int highByteOffset)
	{
		if ((bytes.Length < 4) || ((bytes.Length % 2) != 0))
		{
			return false;
		}

		int lowByteOffset = 1 - highByteOffset;
		for (int index = 0; index < bytes.Length; index += 2)
		{
			if ((bytes[index + highByteOffset] != 0) || (bytes[index + lowByteOffset] == 0))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsReservedExampleDomain(string domain)
	{
		string normalizedDomain = domain.TrimEnd('.').ToLowerInvariant();
		string[] reservedDomains = { "example.com", "example.net", "example.org" };
		if (reservedDomains.Any(reservedDomain =>
			string.Equals(normalizedDomain, reservedDomain, StringComparison.Ordinal) ||
			normalizedDomain.EndsWith($".{reservedDomain}", StringComparison.Ordinal)))
		{
			return true;
		}

		return normalizedDomain.EndsWith(".example", StringComparison.Ordinal) ||
			normalizedDomain.EndsWith(".invalid", StringComparison.Ordinal) ||
			normalizedDomain.EndsWith(".test", StringComparison.Ordinal);
	}

	private static bool LooksLikeRealCredential(string value)
	{
		string normalizedValue = value.ToLowerInvariant();
		if (_placeholderMarkers.Any(normalizedValue.Contains))
		{
			return false;
		}

		return value.Distinct().Take(MinimumCredentialDistinctCharacters).Count() ==
			MinimumCredentialDistinctCharacters;
	}

	private static void ScanPersonalData(string line, int lineNumber, ICollection<PrivacyMatch> matches)
	{
		if (_emailAddressPattern.Matches(line).Cast<Match>().Any(match =>
			!IsReservedExampleDomain(match.Groups["domain"].Value)))
		{
			AddMatch(matches, lineNumber, "non-example email address");
		}

		if (_userHomePathPattern.Matches(line).Cast<Match>().Any(match =>
			!_placeholderUserNames.Contains(match.Groups["user"].Value)))
		{
			AddMatch(matches, lineNumber, "user-specific home path");
		}
	}
}

public sealed record PrivacyMatch(int LineNumber, string Rule);
