using System.Diagnostics;
using System.IO;

namespace AiUsageDashboard.App.Providers;

internal static class CopilotCliExecutableResolver
{
	internal sealed record CandidateSearchResult(
		IReadOnlyList<string> Candidates,
		IReadOnlyList<Exception> Failures);

	private sealed class CandidateSearch
	{
		private readonly Func<string, bool> _isFixedDrivePath;
		private readonly Func<string, FileAttributes> _readAttributes;

		internal List<string> Candidates { get; } = new();
		internal List<Exception> Failures { get; } = new();

		internal CandidateSearch(
			Func<string, bool> isFixedDrivePath,
			Func<string, FileAttributes> readAttributes)
		{
			_isFixedDrivePath = isFixedDrivePath;
			_readAttributes = readAttributes;
		}

		internal void TrySearch(string location, Action search)
		{
			try
			{
				EnsureLocalFixedDrive(location);
				search();
			}
			catch (Exception exception) when (
				(exception is IOException) ||
				(exception is UnauthorizedAccessException) ||
				(exception is ArgumentException) ||
				(exception is NotSupportedException))
			{
				// 單一搜尋位置失敗仍繼續檢查其他候選；全部不可用時一併回報原因。
				Failures.Add(new IOException($"無法檢查 Copilot CLI 搜尋路徑：{location}", exception));
			}
		}

		internal FileAttributes? ReadAttributes(string path)
		{
			EnsureLocalFixedDrive(path);
			try
			{
				return _readAttributes(path);
			}
			catch (Exception exception) when (
				(exception is FileNotFoundException) || (exception is DirectoryNotFoundException))
			{
				return null;
			}
		}

		private void EnsureLocalFixedDrive(string path)
		{
			string fullPath = Path.GetFullPath(path);
			string? root = Path.GetPathRoot(fullPath);
			// UNC 與 device path 必須在任何 metadata 或 drive probe 前拒絕。
			if (!Path.IsPathFullyQualified(path) ||
				(root is null) || (root.Length != 3) ||
				!char.IsAsciiLetter(root[0]) || (root[1] != ':') ||
				!_isFixedDrivePath(fullPath))
			{
				throw new IOException($"Copilot CLI 搜尋只接受本機固定磁碟的絕對路徑：{path}");
			}
		}
	}

	internal const int MaximumWingetPackageDirectories = 64;
	internal const int MaximumPathDirectories = 256;
	private const int MaximumCandidatePaths = 1024;
	private const string WingetPackagePattern = "GitHub.Copilot_Microsoft.Winget.Source_*";
	private static readonly Version MinimumVersion = new(1, 0, 79);

	internal static Task<WindowsOfficialCliExecutableLease?> ResolveAsync(
		Func<WindowsOfficialCliExecutableLease?> resolver,
		CancellationToken cancellationToken)
	{
		return ProviderProcessExecution.RunSynchronousAsync(
			resolver,
			cancellationToken,
			lateResultCleanup: static lease =>
			{
				lease?.Dispose();
				return Task.CompletedTask;
			});
	}

	internal static WindowsOfficialCliExecutableLease? Resolve()
	{
		return Resolve(
			FindCandidates(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				Environment.GetEnvironmentVariable("PATH")),
			CopilotCliExecutableStager.Shared.Stage,
			ReadVersion);
	}

	internal static WindowsOfficialCliExecutableLease? Resolve(
		CandidateSearchResult search,
		Func<string, WindowsOfficialCliExecutableLease> stage,
		Func<string, (string? ProductName, string? FileVersion, string? ProductVersion)> readVersion)
	{
		ArgumentNullException.ThrowIfNull(search);
		return Resolve(search.Candidates, stage, readVersion, search.Failures);
	}

	internal static WindowsOfficialCliExecutableLease? Resolve(
		IEnumerable<string> candidates,
		Func<string, WindowsOfficialCliExecutableLease> stage,
		Func<string, (string? ProductName, string? FileVersion, string? ProductVersion)> readVersion,
		IEnumerable<Exception>? searchFailures = null)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(stage);
		ArgumentNullException.ThrowIfNull(readVersion);
		List<Exception> failures = new(searchFailures ?? []);
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
		int candidateCount = 0;

		foreach (string candidate in candidates)
		{
			if (++candidateCount > MaximumCandidatePaths)
			{
				throw new IOException($"Copilot CLI 候選路徑超過安全上限 {MaximumCandidatePaths}。");
			}

			WindowsOfficialCliExecutableLease? lease = null;

			try
			{
				if (!Path.IsPathFullyQualified(candidate) ||
					!string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException($"Copilot CLI 候選必須是絕對 EXE 路徑：{candidate}");
				}

				string fullPath = Path.GetFullPath(candidate);
				if (!seenPaths.Add(fullPath))
				{
					continue;
				}

				lease = stage(fullPath);
				if (!lease.IsProtected)
				{
					throw new IOException($"Copilot CLI 尚未取得受保護的執行檔鎖：{fullPath}");
				}

				// 只讀已驗簽且仍鎖定的副本；版本檢查不得啟動 CLI。
				(string? productName, string? fileVersion, string? productVersion) =
					readVersion(lease.ExecutablePath);
				if (!HasSupportedVersion(productName, fileVersion, productVersion))
				{
					throw new IOException(
						$"Copilot CLI 產品或版本不相容：{fullPath}；" +
						$"ProductName={productName}, FileVersion={fileVersion}, ProductVersion={productVersion}。" +
						"需要 GitHub Copilot CLI 三段式正式版 1.0.79 以上且低於 2.0.0。");
				}

				WindowsOfficialCliExecutableLease result = lease;
				lease = null;
				return result;
			}
			catch (Exception exception) when (
				(exception is IOException) ||
				(exception is UnauthorizedAccessException) ||
				(exception is WindowsOfficialCliExecutableStagingException) ||
				(exception is ArgumentException) ||
				(exception is NotSupportedException))
			{
				failures.Add(new IOException($"無法使用 Copilot CLI 候選：{candidate}", exception));
			}
			finally
			{
				lease?.Dispose();
			}
		}

		if (failures.Count != 0)
		{
			throw new IOException(
				"無法找到可安全使用的本機 Copilot CLI；搜尋位置或候選的來源、簽章、版本檢查失敗。",
				new AggregateException(failures));
		}

		return null;
	}

	internal static CandidateSearchResult FindCandidates(
		string? localApplicationData,
		string? programFiles,
		string? applicationData,
		string? path,
		Func<string, bool>? isFixedDrivePath = null,
		Func<string, FileAttributes>? readAttributes = null)
	{
		CandidateSearch search = new(
			isFixedDrivePath ?? WindowsExecutablePathSecurity.IsFixedDrivePath,
			readAttributes ?? File.GetAttributes);
		AddWingetCandidates(search, localApplicationData, "Microsoft", "WinGet", "Packages");
		AddWingetCandidates(search, programFiles, "WinGet", "Packages");

		if (!string.IsNullOrWhiteSpace(applicationData) && Path.IsPathFullyQualified(applicationData))
		{
			AddNpmCandidates(search, Path.Combine(applicationData, "npm"));
		}

		string[] pathDirectories = (path ?? string.Empty).Split(Path.PathSeparator);
		if (pathDirectories.Length > MaximumPathDirectories)
		{
			throw new IOException($"Copilot CLI 的 PATH 目錄數超過安全上限 {MaximumPathDirectories}。");
		}

		foreach (string entry in pathDirectories)
		{
			string directory = entry.Trim().Trim('"');
			if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
			{
				continue;
			}

			AddExistingCandidate(search, Path.Combine(directory, "copilot.exe"));
			AddNpmCandidates(search, directory);
		}

		return new CandidateSearchResult(
			search.Candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
			search.Failures.ToArray());
	}

	internal static bool HasSupportedVersion(
		string? productName,
		string? fileVersion,
		string? productVersion)
	{
		return string.Equals(productName, "GitHub Copilot CLI", StringComparison.Ordinal) &&
			string.Equals(fileVersion, productVersion, StringComparison.Ordinal) &&
			Version.TryParse(fileVersion, out Version? version) &&
			(version.Build >= 0) && (version.Revision == -1) &&
			string.Equals(fileVersion, version.ToString(3), StringComparison.Ordinal) &&
			(version >= MinimumVersion) && (version.Major == 1);
	}

	private static (string? ProductName, string? FileVersion, string? ProductVersion) ReadVersion(string path)
	{
		FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
		return (version.ProductName, version.FileVersion, version.ProductVersion);
	}

	private static void AddWingetCandidates(
		CandidateSearch search,
		string? basePath,
		params string[] relativeSegments)
	{
		if (string.IsNullOrWhiteSpace(basePath) || !Path.IsPathFullyQualified(basePath))
		{
			return;
		}

		string root = Path.Combine([basePath, .. relativeSegments]);
		search.TrySearch(root, () =>
		{
			if (!IsOrdinaryDirectory(search, root))
			{
				return;
			}

			EnumerationOptions options = new()
			{
				RecurseSubdirectories = false,
				IgnoreInaccessible = false,
				AttributesToSkip = FileAttributes.ReparsePoint,
				MatchCasing = MatchCasing.CaseInsensitive
			};
			List<string> packages = new();
			foreach (string package in Directory.EnumerateDirectories(root, WingetPackagePattern, options))
			{
				if (packages.Count >= MaximumWingetPackageDirectories)
				{
					throw new IOException(
						$"Copilot WinGet package 目錄超過安全上限 {MaximumWingetPackageDirectories}：{root}");
				}

				packages.Add(package);
			}

			foreach (string package in packages.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
			{
				search.TrySearch(package, () =>
				{
					if (IsOrdinaryDirectory(search, package))
					{
						AddExistingCandidate(search, Path.Combine(package, "copilot.exe"));
					}
				});
			}
		});
	}

	private static void AddNpmCandidates(CandidateSearch search, string prefix)
	{
		AddExistingCandidate(search, Path.Combine(
			prefix, "node_modules", "@github", "copilot-win32-x64", "copilot.exe"));
		AddExistingCandidate(search, Path.Combine(
			prefix, "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win32-x64", "copilot.exe"));
	}

	private static void AddExistingCandidate(CandidateSearch search, string path)
	{
		search.TrySearch(path, () =>
		{
			FileAttributes? attributes = search.ReadAttributes(path);
			if ((attributes is not null) && ((attributes.Value & FileAttributes.Directory) == 0))
			{
				search.Candidates.Add(Path.GetFullPath(path));
			}
		});
	}

	private static bool IsOrdinaryDirectory(CandidateSearch search, string path)
	{
		FileAttributes? attributes = search.ReadAttributes(path);
		return (attributes is not null) &&
			((attributes.Value & FileAttributes.Directory) != 0) &&
			WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory(path);
	}

}
