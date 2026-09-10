using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.Licensing;
using AiUsageDashboard.PrivacyCheck;

namespace AiUsageDashboard.Tests;

public sealed class RepositoryPrivacyTests
{
	private static readonly HashSet<string> ExcludedDirectoryNames = new(
		new[] { ".git", "bin", "obj", "work" },
		StringComparer.OrdinalIgnoreCase);

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
			if (PrivacyRules.IsSensitiveArtifactName(fileName))
			{
				violations.Add($"{relativePath}: sensitive artifact filename");
				continue;
			}

			string absolutePath = Path.Combine(repositoryRoot, relativePath);
			byte[] bytes = File.ReadAllBytes(absolutePath);
			bool isOriginalNotice = originalNoticeHashes.TryGetValue(
				relativePath.Replace('\\', '/'), out string? expectedNoticeHash);
			if (isOriginalNotice)
			{
				Assert.Equal(expectedNoticeHash,
					Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
			}

			if (!PrivacyRules.TryDecodeText(bytes, out string text))
			{
				if (PrivacyRules.IsKnownTextFile(fileName))
				{
					violations.Add($"{relativePath}: source text cannot be decoded as valid UTF-8 or UTF-16");
				}
				continue;
			}

			// 原文 notices 的個資例外只適用於上方已逐 byte 驗證的版本；secret 規則仍執行。
			foreach (PrivacyMatch match in PrivacyRules.ScanText(text, includePersonalData: !isOriginalNotice))
			{
				violations.Add($"{relativePath}:{match.LineNumber}: {match.Rule}");
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
}
