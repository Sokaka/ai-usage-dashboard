using System.IO;
using System.Runtime.InteropServices;

namespace AiUsageDashboard.App.Providers;

internal static class CodexOfficialExecutablePathResolver
{
	internal static string Resolve(string executablePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

		try
		{
			string fullPath = Path.GetFullPath(executablePath);
			string localApplicationData = Environment.GetFolderPath(
				Environment.SpecialFolder.LocalApplicationData);
			string userProfile = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(localApplicationData) ||
				string.IsNullOrWhiteSpace(userProfile))
			{
				return fullPath;
			}

			string defaultVisibleBinDirectory = Path.Combine(
				localApplicationData,
				"Programs",
				"OpenAI",
				"Codex",
				"bin");
			string? configuredInstallDirectory =
				Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR");
			string? pathValue = Environment.GetEnvironmentVariable("PATH");

			if (!IsExpectedVisibleExecutablePath(
					fullPath,
					defaultVisibleBinDirectory,
					configuredInstallDirectory,
					pathValue))
			{
				return fullPath;
			}

			string? configuredCodexHome =
				Environment.GetEnvironmentVariable("CODEX_HOME");
			string codexHome = string.IsNullOrWhiteSpace(configuredCodexHome)
				? Path.Combine(userProfile, ".codex")
				: configuredCodexHome;
			string visibleBinDirectory = Path.GetDirectoryName(fullPath) ??
				throw CreateUnsafePathException();

			return Resolve(
				fullPath,
				visibleBinDirectory,
				codexHome,
				GetExpectedWindowsTarget());
		}
		catch (OfficialCliExecutableValidationException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw CreateUnsafePathException(exception);
		}
	}

	internal static string ResolveStagingSource(string executablePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

		try
		{
			string fullPath = Path.GetFullPath(executablePath);
			string localApplicationData = Environment.GetFolderPath(
				Environment.SpecialFolder.LocalApplicationData);
			string userProfile = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(localApplicationData) ||
				string.IsNullOrWhiteSpace(userProfile))
			{
				return fullPath;
			}

			string defaultVisibleBinDirectory = Path.Combine(
				localApplicationData,
				"Programs",
				"OpenAI",
				"Codex",
				"bin");
			string? configuredInstallDirectory =
				Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR");
			string? pathValue = Environment.GetEnvironmentVariable("PATH");

			if (!IsExpectedVisibleExecutablePath(
					fullPath,
					defaultVisibleBinDirectory,
					configuredInstallDirectory,
					pathValue))
			{
				return fullPath;
			}

			string? configuredCodexHome =
				Environment.GetEnvironmentVariable("CODEX_HOME");
			string codexHome = string.IsNullOrWhiteSpace(configuredCodexHome)
				? Path.Combine(userProfile, ".codex")
				: configuredCodexHome;
			string visibleBinDirectory = Path.GetDirectoryName(fullPath) ??
				throw CreateUnsafePathException();

			return ResolveStagingSource(
				fullPath,
				visibleBinDirectory,
				codexHome,
				GetExpectedWindowsTarget());
		}
		catch (OfficialCliExecutableValidationException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw CreateUnsafePathException(exception);
		}
	}

	internal static string Resolve(
		string executablePath,
		string visibleBinDirectory,
		string codexHome,
		string expectedWindowsTarget)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(visibleBinDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedWindowsTarget);

		try
		{
			string fullPath = Path.GetFullPath(executablePath);
			string visibleBin = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(visibleBinDirectory));
			string codexHomeRoot = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(codexHome));
			string visibleRoot = Path.GetDirectoryName(visibleBin) ??
				throw CreateUnsafePathException();

			if (!IsExecutableInVisibleDirectory(fullPath, visibleBin) ||
				!WindowsExecutablePathSecurity.IsFixedDrivePath(fullPath) ||
				!WindowsExecutablePathSecurity.IsFixedDrivePath(visibleRoot) ||
				!WindowsExecutablePathSecurity.IsFixedDrivePath(codexHomeRoot))
			{
				throw CreateUnsafePathException();
			}

			string standaloneRoot = Path.Combine(
				codexHomeRoot,
				"packages",
				"standalone");

			return Resolve(
				fullPath,
				standaloneRoot,
				expectedWindowsTarget,
				WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
				WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory,
				path => IsPathAclSafeWithinInstallerRoots(
					path,
					visibleRoot,
					codexHomeRoot),
				path => IsDirectoryPathAclSafeWithinInstallerRoots(
					path,
					visibleRoot,
					codexHomeRoot));
		}
		catch (OfficialCliExecutableValidationException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw CreateUnsafePathException(exception);
		}
	}

	internal static string ResolveStagingSource(
		string executablePath,
		string visibleBinDirectory,
		string codexHome,
		string expectedWindowsTarget)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(visibleBinDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedWindowsTarget);

		try
		{
			string fullPath = Path.GetFullPath(executablePath);
			string visibleBin = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(visibleBinDirectory));
			string codexHomeRoot = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(codexHome));
			string visibleRoot = Path.GetDirectoryName(visibleBin) ??
				throw CreateUnsafePathException();

			if (!IsExecutableInVisibleDirectory(fullPath, visibleBin) ||
				!WindowsExecutablePathSecurity.IsFixedDrivePath(fullPath) ||
				!WindowsExecutablePathSecurity.IsFixedDrivePath(visibleRoot) ||
				!WindowsExecutablePathSecurity.IsFixedDrivePath(codexHomeRoot))
			{
				throw CreateUnsafePathException();
			}

			string standaloneRoot = Path.Combine(
				codexHomeRoot,
				"packages",
				"standalone");

			return ResolveStagingSource(
				fullPath,
				standaloneRoot,
				expectedWindowsTarget,
				WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
				WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory);
		}
		catch (OfficialCliExecutableValidationException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw CreateUnsafePathException(exception);
		}
	}

	internal static bool IsResolvedExecutablePathAclSafe(string executablePath)
	{
		try
		{
			string localApplicationData = Environment.GetFolderPath(
				Environment.SpecialFolder.LocalApplicationData);
			string userProfile = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(localApplicationData) ||
				string.IsNullOrWhiteSpace(userProfile))
			{
				return false;
			}

			string defaultVisibleBinDirectory = Path.Combine(
				localApplicationData,
				"Programs",
				"OpenAI",
				"Codex",
				"bin");
			string? configuredInstallDirectory =
				Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR");
			string? configuredCodexHome =
				Environment.GetEnvironmentVariable("CODEX_HOME");
			string codexHome = string.IsNullOrWhiteSpace(configuredCodexHome)
				? Path.Combine(userProfile, ".codex")
				: configuredCodexHome;

			return IsResolvedExecutablePathAclSafe(
				executablePath,
				defaultVisibleBinDirectory,
				configuredInstallDirectory,
				codexHome);
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsResolvedExecutablePathAclSafe(
		string executablePath,
		string defaultVisibleBinDirectory,
		string? configuredInstallDirectory,
		string codexHome)
	{
		try
		{
			string fullPath = Path.GetFullPath(executablePath);
			string codexHomeRoot = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(codexHome));

			if (WindowsExecutablePathSecurity.IsPathAclSafe(fullPath) ||
				WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot(
					fullPath,
					codexHomeRoot))
			{
				return true;
			}

			foreach (string? visibleBinDirectory in new[]
				{
					defaultVisibleBinDirectory,
					configuredInstallDirectory
				})
			{
				if (!IsExecutableInVisibleDirectory(
						fullPath,
						visibleBinDirectory))
				{
					continue;
				}

				string visibleBin = Path.TrimEndingDirectorySeparator(
					Path.GetFullPath(visibleBinDirectory!));
				string? visibleRoot = Path.GetDirectoryName(visibleBin);

				return !string.IsNullOrWhiteSpace(visibleRoot) &&
					WindowsExecutablePathSecurity
						.IsPathAclSafeWithinTrustedRoot(
							fullPath,
							visibleRoot);
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsExpectedVisibleExecutablePath(
		string executablePath,
		string defaultVisibleBinDirectory,
		string? configuredInstallDirectory,
		string? pathValue)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultVisibleBinDirectory);

		if (IsExecutableInVisibleDirectory(
				executablePath,
				defaultVisibleBinDirectory) ||
			IsExecutableInVisibleDirectory(
				executablePath,
				configuredInstallDirectory))
		{
			return true;
		}

		if (string.IsNullOrWhiteSpace(pathValue))
		{
			return false;
		}

		foreach (string pathEntry in pathValue.Split(Path.PathSeparator))
		{
			if (IsExecutableInVisibleDirectory(
					executablePath,
					pathEntry.Trim().Trim('"')))
			{
				return true;
			}
		}

		return false;
	}

	internal static string Resolve(
		string executablePath,
		string standaloneRoot,
		string expectedWindowsTarget,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isCanonicalNonReparseDirectory,
		Func<string, bool> isPathAclSafe,
		Func<string, bool> isDirectoryPathAclSafe)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(standaloneRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedWindowsTarget);
		ArgumentNullException.ThrowIfNull(isCanonicalNonReparseFile);
		ArgumentNullException.ThrowIfNull(isCanonicalNonReparseDirectory);
		ArgumentNullException.ThrowIfNull(isPathAclSafe);
		ArgumentNullException.ThrowIfNull(isDirectoryPathAclSafe);

		try
		{
			string fullPath = Path.GetFullPath(executablePath);
			string visibleBinPath = Path.GetDirectoryName(fullPath) ??
				throw CreateUnsafePathException();
			DirectoryInfo visibleBin = new(visibleBinPath);

			if (!visibleBin.Exists ||
				((visibleBin.Attributes & FileAttributes.ReparsePoint) == 0))
			{
				return fullPath;
			}

			string? visibleRootPath = visibleBin.Parent?.FullName;

			if (string.IsNullOrWhiteSpace(visibleRootPath) ||
				!isCanonicalNonReparseDirectory(visibleRootPath) ||
				!isDirectoryPathAclSafe(visibleRootPath))
			{
				throw CreateUnsafePathException();
			}

			string standalonePath = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(standaloneRoot));
			string releasesPath = Path.Combine(standalonePath, "releases");
			string currentPath = Path.Combine(standalonePath, "current");

			if (!isCanonicalNonReparseDirectory(standalonePath) ||
				!isDirectoryPathAclSafe(standalonePath) ||
				!isCanonicalNonReparseDirectory(releasesPath) ||
				!isDirectoryPathAclSafe(releasesPath))
			{
				throw CreateUnsafePathException();
			}

			DirectoryInfo current = new(currentPath);

			if (!current.Exists ||
				((current.Attributes & FileAttributes.ReparsePoint) == 0))
			{
				throw CreateUnsafePathException();
			}

			DirectoryInfo visibleImmediateTarget =
				ResolveDirectoryLink(visibleBin, returnFinalTarget: false);
			DirectoryInfo visibleFinalTarget =
				ResolveDirectoryLink(visibleBin, returnFinalTarget: true);
			DirectoryInfo currentImmediateTarget =
				ResolveDirectoryLink(current, returnFinalTarget: false);
			DirectoryInfo currentFinalTarget =
				ResolveDirectoryLink(current, returnFinalTarget: true);
			string releasePath = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(currentImmediateTarget.FullName));

			if (!PathsEqual(currentFinalTarget.FullName, releasePath) ||
				!IsDirectReleaseDirectory(
					releasePath,
					releasesPath,
					expectedWindowsTarget))
			{
				throw CreateUnsafePathException();
			}

			string packageVisibleTarget = Path.Combine(currentPath, "bin");
			bool isPackageLayout = PathsEqual(
				visibleImmediateTarget.FullName,
				packageVisibleTarget);
			bool isLegacyLayout = PathsEqual(
				visibleImmediateTarget.FullName,
				currentPath);

			if (!isPackageLayout && !isLegacyLayout)
			{
				throw CreateUnsafePathException();
			}

			string expectedVisibleFinalTarget = isPackageLayout
				? Path.Combine(releasePath, "bin")
				: releasePath;

			if (!PathsEqual(
				visibleFinalTarget.FullName,
				expectedVisibleFinalTarget))
			{
				throw CreateUnsafePathException();
			}

			string physicalExecutablePath = Path.Combine(
				expectedVisibleFinalTarget,
				"codex.exe");

			if (!File.Exists(physicalExecutablePath) ||
				!isCanonicalNonReparseFile(physicalExecutablePath) ||
				!isPathAclSafe(physicalExecutablePath))
			{
				throw CreateUnsafePathException();
			}

			return Path.GetFullPath(physicalExecutablePath);
		}
		catch (OfficialCliExecutableValidationException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw CreateUnsafePathException(exception);
		}
	}

	internal static string ResolveStagingSource(
		string executablePath,
		string standaloneRoot,
		string expectedWindowsTarget,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isCanonicalNonReparseDirectory)
	{
		return Resolve(
			executablePath,
			standaloneRoot,
			expectedWindowsTarget,
			isCanonicalNonReparseFile,
			isCanonicalNonReparseDirectory,
			_ => true,
			_ => true);
	}

	private static OfficialCliExecutableValidationException
		CreateUnsafePathException(Exception? innerException = null)
	{
		return new OfficialCliExecutableValidationException(
			OfficialCliExecutableValidationFailureReason.UnsafePath,
			"Codex official installer link layout is invalid.",
			innerException);
	}

	private static string GetExpectedWindowsTarget()
	{
		return RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "x86_64-pc-windows-msvc",
			Architecture.Arm64 => "aarch64-pc-windows-msvc",
			_ => throw CreateUnsafePathException()
		};
	}

	private static bool IsDirectReleaseDirectory(
		string releasePath,
		string releasesPath,
		string expectedWindowsTarget)
	{
		DirectoryInfo release = new(releasePath);
		string suffix = $"-{expectedWindowsTarget}";

		if (!release.Exists ||
			release.Parent is null ||
			!PathsEqual(release.Parent.FullName, releasesPath) ||
			!release.Name.EndsWith(suffix, StringComparison.Ordinal))
		{
			return false;
		}

		string versionText = release.Name[..^suffix.Length];
		return CodexCliVersionProbe.TryParseVersion(
			$"codex-cli {versionText}",
			out _);
	}

	private static bool IsExecutableInVisibleDirectory(
		string executablePath,
		string? visibleDirectory)
	{
		try
		{
			return !string.IsNullOrWhiteSpace(visibleDirectory) &&
				Path.IsPathFullyQualified(visibleDirectory) &&
				PathsEqual(
					executablePath,
					Path.Combine(visibleDirectory, "codex.exe"));
		}
		catch
		{
			return false;
		}
	}

	private static bool IsDirectoryPathAclSafeWithinInstallerRoots(
		string path,
		string visibleRoot,
		string codexHome)
	{
		return WindowsExecutablePathSecurity
			.IsDirectoryPathAclSafeWithinTrustedRoot(path, visibleRoot) ||
			WindowsExecutablePathSecurity
				.IsDirectoryPathAclSafeWithinTrustedRoot(path, codexHome);
	}

	private static bool IsPathAclSafeWithinInstallerRoots(
		string path,
		string visibleRoot,
		string codexHome)
	{
		return WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot(
			path,
			visibleRoot) ||
			WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot(
				path,
				codexHome);
	}

	private static bool PathsEqual(string left, string right)
	{
		return string.Equals(
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
			StringComparison.OrdinalIgnoreCase);
	}

	private static DirectoryInfo ResolveDirectoryLink(
		DirectoryInfo directory,
		bool returnFinalTarget)
	{
		return directory.ResolveLinkTarget(returnFinalTarget) as DirectoryInfo ??
			throw CreateUnsafePathException();
	}
}
