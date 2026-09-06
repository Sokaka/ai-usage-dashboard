using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using AiUsageDashboard.Licensing;

namespace AiUsageDashboard.Tests;

public sealed class RepositoryPrivacyTests
{
	private sealed record SecretRule(string Description, Regex Pattern);

	private static readonly HashSet<string> ExcludedDirectoryNames = new(
		new[] { ".git", "bin", "obj", "work" },
		StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> PlaceholderUserNames = new(
		new[]
		{
			"alice",
			"bob",
			"default",
			"example",
			"placeholder",
			"public",
			"runner",
			"sample",
			"synthetic",
			"test",
			"tester",
			"user",
			"username"
		},
		StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> TextFileExtensions = new(
		new[]
		{
			".bat",
			".cmd",
			".config",
			".cs",
			".csproj",
			".json",
			".md",
			".props",
			".ps1",
			".resx",
			".sh",
			".sln",
			".targets",
			".toml",
			".txt",
			".xaml",
			".xml",
			".yaml",
			".yml"
		},
		StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> SensitiveFileNames = new(
		new[]
		{
			".env",
			"accounts.json",
			"auth.json",
			"credentials.json",
			"preferences.json",
			"secrets.json"
		},
		StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> SensitiveFileExtensions = new(
		new[] { ".jwk", ".key", ".p12", ".pem", ".pfx", ".pk8" },
		StringComparer.OrdinalIgnoreCase);

	private static readonly Regex CredentialAssignmentPattern = new(
		@"[""']?(?<name>api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|auth[_-]?token|password|passwd)[""']?\s*(?:=|:)\s*[""']?(?<value>[A-Z0-9_./+\-=:]{24,})",
		RegexOptions.Compiled |
		RegexOptions.CultureInvariant |
		RegexOptions.IgnoreCase);

	private static readonly Regex EmailAddressPattern = new(
		@"(?<![A-Z0-9._%+\-])(?<local>[A-Z0-9._%+\-]{1,64})@(?<domain>(?:[A-Z0-9](?:[A-Z0-9\-]{0,61}[A-Z0-9])?\.)+[A-Z]{2,63})(?![A-Z0-9.\-])",
		RegexOptions.Compiled |
		RegexOptions.CultureInvariant |
		RegexOptions.IgnoreCase);

	private static readonly Regex UserHomePathPattern = new(
		@"(?:(?:[A-Z]:)?[\\/](?:Users|home)[\\/])(?<user>[^\\/\s""'<>:$%{}]+)",
		RegexOptions.Compiled |
		RegexOptions.CultureInvariant |
		RegexOptions.IgnoreCase);

	private static readonly SecretRule[] SecretRules =
	{
		new(
			"private-key material",
			new Regex(
				Regex.Escape("-----" + "BEGIN") +
				@"(?: [A-Z0-9]+)? PRIVATE KEY" +
				Regex.Escape("-----"),
				RegexOptions.Compiled |
				RegexOptions.CultureInvariant |
				RegexOptions.IgnoreCase)),
		new(
			"provider API key",
			new Regex(
				@"\bs" + @"k-(?:ant-|proj-)?[A-Z0-9_-]{20,}\b",
				RegexOptions.Compiled |
				RegexOptions.CultureInvariant |
				RegexOptions.IgnoreCase)),
		new(
			"GitHub access token",
			new Regex(
				@"\b(?:g" + @"h[pousr]_[A-Z0-9]{30,}|g" +
				@"ithub_pat_[A-Z0-9_]{40,})\b",
				RegexOptions.Compiled |
				RegexOptions.CultureInvariant |
				RegexOptions.IgnoreCase)),
		new(
			"AWS access key",
			new Regex(
				@"\b(?:A" + @"KIA|A" + @"SIA)[A-Z0-9]{16}\b",
				RegexOptions.Compiled |
				RegexOptions.CultureInvariant)),
		new(
			"Google API key",
			new Regex(
				@"\bA" + @"Iza[A-Z0-9_-]{35}\b",
				RegexOptions.Compiled |
				RegexOptions.CultureInvariant)),
		new(
			"Slack access token",
			new Regex(
				@"\bx" + @"ox[baprs]-[A-Z0-9-]{20,}\b",
				RegexOptions.Compiled |
				RegexOptions.CultureInvariant |
				RegexOptions.IgnoreCase)),
		new(
			"credential embedded in URL",
			new Regex(
				@"\bhttps?://[^/\s:@]+:[^@\s/]+@",
				RegexOptions.Compiled |
				RegexOptions.CultureInvariant |
				RegexOptions.IgnoreCase))
	};

	[Fact]
	public void GitIgnore_ProtectsPrivateKeysAndPerUserArtifacts()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
		HashSet<string> rules = File.ReadLines(gitIgnorePath)
			.Select(line => line.Trim())
			.Where(line => (line.Length > 0) && !line.StartsWith('#'))
			.ToHashSet(StringComparer.Ordinal);
		string[] requiredRules =
		{
			"*.key",
			"*.pem",
			"*.pk8",
			"*.jwk",
			"credentials.json",
			"auth.json",
			"accounts.json",
			"preferences.json",
			"usage-snapshot*.json",
			"agy-profile*.json",
			"agy-*.profile.json",
			"antigravity-profile*.json",
			"*.private-profile.json",
			"*.profile.private.json"
		};

		foreach (string requiredRule in requiredRules)
		{
			Assert.Contains(requiredRule, rules);
		}
	}

	[Fact]
	public void SourceBoundary_DoesNotContainPersonalDataOrHighConfidenceSecrets()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		List<string> violations = new();
		Dictionary<string, string> originalNoticeHashes = LegalCatalog.Load(LegalProfile.Installer)
			.Documents.Where(document => document.Path.StartsWith("third-party-notices/", StringComparison.Ordinal))
			.ToDictionary(document => document.Path, document => document.Sha256, StringComparer.Ordinal);

		foreach (string relativePath in GetSourceBoundaryFiles(repositoryRoot))
		{
			if (IsExcludedPath(relativePath))
			{
				continue;
			}

			string fileName = Path.GetFileName(relativePath);
			if (IsSensitiveArtifactName(fileName))
			{
				violations.Add($"{relativePath}: sensitive artifact filename");
				continue;
			}

			if (!IsTextFile(fileName))
			{
				continue;
			}

			string absolutePath = Path.Combine(repositoryRoot, relativePath);
			bool isOriginalNotice = originalNoticeHashes.TryGetValue(
				relativePath.Replace('\\', '/'), out string? expectedNoticeHash);
			if (isOriginalNotice)
			{
				Assert.Equal(expectedNoticeHash,
					Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath))).ToLowerInvariant());
			}
			int lineNumber = 0;
			foreach (string line in File.ReadLines(absolutePath))
			{
				lineNumber++;
				ScanLine(relativePath, lineNumber, line, violations, isOriginalNotice);
			}
		}

		Assert.True(
			violations.Count == 0,
			BuildViolationMessage(violations));
	}

	private static string BuildViolationMessage(IReadOnlyList<string> violations)
	{
		const int MaximumReportedViolations = 50;

		if (violations.Count == 0)
		{
			return string.Empty;
		}

		IEnumerable<string> reported = violations.Take(MaximumReportedViolations);
		string suffix = violations.Count > MaximumReportedViolations
			? $"{Environment.NewLine}... and {violations.Count - MaximumReportedViolations} more."
			: string.Empty;
		return
			"Repository privacy guard found files that need review. " +
			"Matched values are intentionally hidden." +
			Environment.NewLine +
			string.Join(Environment.NewLine, reported) +
			suffix;
	}

	private static IReadOnlyList<string> GetSourceBoundaryFiles(
		string repositoryRoot)
	{
		string gitPath = Path.Combine(repositoryRoot, ".git");
		if (!File.Exists(gitPath) && !Directory.Exists(gitPath))
		{
			return EnumerateSnapshotFiles(repositoryRoot, repositoryRoot)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = "git",
			WorkingDirectory = repositoryRoot,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("ls-files");
		startInfo.ArgumentList.Add("--cached");
		startInfo.ArgumentList.Add("--others");
		startInfo.ArgumentList.Add("--exclude-standard");
		startInfo.ArgumentList.Add("-z");

		using Process process = Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"Could not start Git for repository privacy validation.");
		string output = process.StandardOutput.ReadToEnd();
		_ = process.StandardError.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				"Git could not enumerate the repository source boundary.");
		}

		return output
			.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Where(relativePath =>
				File.Exists(Path.Combine(repositoryRoot, relativePath)))
			.OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static IEnumerable<string> EnumerateSnapshotFiles(
		string repositoryRoot,
		string directoryPath)
	{
		foreach (string entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
		{
			FileAttributes attributes = File.GetAttributes(entryPath);
			if ((attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new InvalidOperationException(
					$"Source snapshot contains a reparse point: {entryPath}");
			}

			if ((attributes & FileAttributes.Directory) != 0)
			{
				if (!ExcludedDirectoryNames.Contains(Path.GetFileName(entryPath)))
				{
					foreach (string relativePath in EnumerateSnapshotFiles(repositoryRoot, entryPath))
					{
						yield return relativePath;
					}
				}
			}
			else
			{
				yield return Path.GetRelativePath(repositoryRoot, entryPath);
			}
		}
	}

	private static bool IsExcludedPath(string relativePath)
	{
		return relativePath
			.Replace('\\', '/')
			.Split('/', StringSplitOptions.RemoveEmptyEntries)
			.Any(ExcludedDirectoryNames.Contains);
	}

	private static bool IsReservedExampleDomain(string domain)
	{
		string normalizedDomain = domain.TrimEnd('.').ToLowerInvariant();
		string[] reservedDomains =
		{
			"example.com",
			"example.net",
			"example.org"
		};

		if (reservedDomains.Any(reservedDomain =>
			string.Equals(
				normalizedDomain,
				reservedDomain,
				StringComparison.Ordinal) ||
			normalizedDomain.EndsWith(
				$".{reservedDomain}",
				StringComparison.Ordinal)))
		{
			return true;
		}

		return normalizedDomain.EndsWith(".example", StringComparison.Ordinal) ||
			normalizedDomain.EndsWith(".invalid", StringComparison.Ordinal) ||
			normalizedDomain.EndsWith(".test", StringComparison.Ordinal);
	}

	private static bool IsSensitiveArtifactName(string fileName)
	{
		if (SensitiveFileNames.Contains(fileName) ||
			SensitiveFileExtensions.Contains(Path.GetExtension(fileName)))
		{
			return true;
		}

		return fileName.StartsWith(
				"accounts.corrupt-",
				StringComparison.OrdinalIgnoreCase) ||
			fileName.StartsWith(
				"usage-snapshot",
				StringComparison.OrdinalIgnoreCase) ||
			fileName.StartsWith(
				"agy-profile",
				StringComparison.OrdinalIgnoreCase) ||
			(fileName.StartsWith(
					"agy-",
					StringComparison.OrdinalIgnoreCase) &&
				fileName.EndsWith(
					".profile.json",
					StringComparison.OrdinalIgnoreCase)) ||
			fileName.StartsWith(
				"antigravity-profile",
				StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(
				".private-profile.json",
				StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(
				".profile.private.json",
				StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsTextFile(string fileName)
	{
		return string.Equals(
				fileName,
				".editorconfig",
				StringComparison.OrdinalIgnoreCase) ||
			string.Equals(
				fileName,
				".gitignore",
				StringComparison.OrdinalIgnoreCase) ||
			string.Equals(
				fileName,
				"LICENSE",
				StringComparison.OrdinalIgnoreCase) ||
			TextFileExtensions.Contains(Path.GetExtension(fileName));
	}

	private static bool LooksLikeRealCredential(string value)
	{
		string normalizedValue = value.ToLowerInvariant();
		string[] placeholderMarkers =
		{
			"changeme",
			"dummy",
			"example",
			"fake",
			"not-a-",
			"placeholder",
			"redacted",
			"replace",
			"sample",
			"secret-value",
			"synthetic",
			"test",
			"token-value",
			"your-",
			"xxx"
		};

		if (placeholderMarkers.Any(normalizedValue.Contains))
		{
			return false;
		}

		return value.Distinct().Take(8).Count() == 8;
	}

	private static void ScanLine(
		string relativePath,
		int lineNumber,
		string line,
		ICollection<string> violations,
		bool isOriginalNotice)
	{
		// 原文 notices 的著作人聯絡資訊必須保留，例外只適用於逐 byte 核對過的文件。
		if (!isOriginalNotice && EmailAddressPattern
			.Matches(line)
			.Cast<Match>()
			.Any(match =>
				!IsReservedExampleDomain(match.Groups["domain"].Value)))
		{
			violations.Add(
				$"{relativePath}:{lineNumber}: non-example email address");
		}

		Match userHomeMatch = UserHomePathPattern.Match(line);
		if (!isOriginalNotice && userHomeMatch.Success &&
			!PlaceholderUserNames.Contains(
				userHomeMatch.Groups["user"].Value))
		{
			violations.Add(
				$"{relativePath}:{lineNumber}: user-specific home path");
		}

		Match credentialAssignmentMatch =
			CredentialAssignmentPattern.Match(line);
		if (credentialAssignmentMatch.Success &&
			LooksLikeRealCredential(
				credentialAssignmentMatch.Groups["value"].Value))
		{
			violations.Add(
				$"{relativePath}:{lineNumber}: credential-like assignment");
		}

		foreach (SecretRule rule in SecretRules)
		{
			if (rule.Pattern.IsMatch(line))
			{
				violations.Add(
					$"{relativePath}:{lineNumber}: {rule.Description}");
			}
		}
	}
}
