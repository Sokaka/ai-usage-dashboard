using System.IO;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal sealed class CopilotCliExecutableStager : IDisposable
{
	private readonly WindowsOfficialCliExecutableStager _stager;

	internal static CopilotCliExecutableStager Shared { get; } = new();

	internal CopilotCliExecutableStager()
		: this(
			ResolveDefaultTrustedRoot,
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot,
			WindowsExecutablePathSecurity.IsDirectoryPathAclSafeWithinTrustedRoot,
			TryPrepareTrustedRoot,
			AntigravityPrivateKeyAcl.TryProtectNewFile)
	{
	}

	internal CopilotCliExecutableStager(
		Func<string> trustedRootResolver,
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isFixedDrivePath,
		Func<string, string, bool> isPathAclSafe,
		Func<string, string, bool> isRootAclSafe,
		Func<string, bool> tryPrepareTrustedRoot,
		Func<string, bool> tryProtectFile)
	{
		_stager = new WindowsOfficialCliExecutableStager(
			"GitHub, Inc.",
			"copilot-",
			".copilot-stage-",
			trustedRootResolver,
			inspectSignature,
			isCanonicalNonReparseFile,
			isFixedDrivePath,
			isPathAclSafe,
			isRootAclSafe,
			tryPrepareTrustedRoot,
			tryProtectFile);
	}

	internal WindowsOfficialCliExecutableLease Stage(string sourceExecutablePath)
	{
		return _stager.Stage(sourceExecutablePath);
	}

	public void Dispose()
	{
		_stager.Dispose();
	}

	internal static string ResolveDefaultTrustedRoot()
	{
		string? fixedDriveRoot = Path.GetPathRoot(Environment.SystemDirectory);
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		string? currentUserSid = identity.User?.Value;

		if (string.IsNullOrWhiteSpace(fixedDriveRoot) ||
			!Path.IsPathFullyQualified(fixedDriveRoot) ||
			string.IsNullOrWhiteSpace(currentUserSid))
		{
			throw new IOException("無法確認 Copilot CLI 的受保護執行目錄與 Windows 使用者。");
		}

		return Path.Combine(
			fixedDriveRoot,
			$"AiUsageDashboard.CopilotCli.{currentUserSid}",
			"executables-v1");
	}

	private static bool TryPrepareTrustedRoot(string trustedRoot)
	{
		return AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
			trustedRoot,
			out _,
			out _);
	}
}
