using System.IO;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal interface IClaudeCliExecutableStager
{
	WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default);
}

internal sealed class ClaudeCliExecutableStager :
	IClaudeCliExecutableStager,
	IDisposable
{
	internal const long MaximumExecutableSizeBytes =
		WindowsOfficialCliExecutableStager.MaximumExecutableSizeBytes;
	private const string ExpectedSignerName = "Anthropic, PBC";
	private const string StagedFilePrefix = "claude-";
	private const string TemporaryFilePrefix = ".claude-stage-";
	private readonly WindowsOfficialCliExecutableStager _stager;

	internal static IClaudeCliExecutableStager Shared { get; } =
		new ClaudeCliExecutableStager();

	internal ClaudeCliExecutableStager()
		: this(
			ResolveDefaultTrustedRoot,
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot,
			WindowsExecutablePathSecurity
				.IsDirectoryPathAclSafeWithinTrustedRoot,
			TryPrepareTrustedRoot,
			AntigravityPrivateKeyAcl.TryProtectNewFile)
	{
	}

	internal ClaudeCliExecutableStager(
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
			ExpectedSignerName,
			StagedFilePrefix,
			TemporaryFilePrefix,
			trustedRootResolver,
			inspectSignature,
			isCanonicalNonReparseFile,
			isFixedDrivePath,
			isPathAclSafe,
			isRootAclSafe,
			tryPrepareTrustedRoot,
			tryProtectFile);
	}

	public WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default)
	{
		try
		{
			return _stager.Stage(sourceExecutablePath, cancellationToken);
		}
		catch (WindowsOfficialCliExecutableStagingException exception)
		{
			throw new ClaudeCliUntrustedException(
				"無法建立或驗證 Claude Code CLI 的受保護執行副本。",
				exception);
		}
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
			throw new ClaudeCliUntrustedException(
				"無法確認 Claude Code CLI 的受保護執行目錄。");
		}

		return Path.Combine(
			fixedDriveRoot,
			$"AiUsageDashboard.ClaudeCli.{currentUserSid}",
			"executables-v1");
	}

	internal static bool IsDefaultStagedPathAclSafe(string absolutePath)
	{
		return WindowsExecutablePathSecurity.IsPathAclSafeWithinTrustedRoot(
			absolutePath,
			ResolveDefaultTrustedRoot());
	}

	private static bool TryPrepareTrustedRoot(string trustedRoot)
	{
		return AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
			trustedRoot,
			out _,
			out _);
	}
}
