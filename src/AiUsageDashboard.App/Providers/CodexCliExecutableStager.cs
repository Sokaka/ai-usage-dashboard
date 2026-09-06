using System.IO;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal interface ICodexCliExecutableStager
{
	WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default);
}

internal sealed class CodexCliExecutableStager :
	ICodexCliExecutableStager,
	IDisposable
{
	internal const long MaximumExecutableSizeBytes =
		WindowsOfficialCliExecutableStager.MaximumExecutableSizeBytes;
	private const string ExpectedSignerName = "OpenAI OpCo, LLC";
	private const string StagedFilePrefix = "codex-";
	private const string TemporaryFilePrefix = ".codex-stage-";
	private readonly WindowsOfficialCliExecutableStager _stager;

	internal static ICodexCliExecutableStager Shared { get; } =
		new CodexCliExecutableStager();

	internal CodexCliExecutableStager()
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

	internal CodexCliExecutableStager(
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
			throw new CodexCliUntrustedException(
				"無法建立或驗證 Codex CLI 的受保護執行副本。",
				exception);
		}
	}

	public void Dispose()
	{
		_stager.Dispose();
	}

	internal static string ResolveDefaultTrustedRoot()
	{
		return Path.Combine(
			ResolveDefaultProviderRoot(),
			"executables-v1");
	}

	internal static string ResolveDefaultProviderRoot()
	{
		string? fixedDriveRoot = Path.GetPathRoot(Environment.SystemDirectory);
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		string? currentUserSid = identity.User?.Value;

		if (string.IsNullOrWhiteSpace(fixedDriveRoot) ||
			!Path.IsPathFullyQualified(fixedDriveRoot) ||
			string.IsNullOrWhiteSpace(currentUserSid))
		{
			throw new CodexCliUntrustedException(
				"無法確認 Codex CLI 的受保護執行目錄。");
		}

		return Path.Combine(
			fixedDriveRoot,
			$"AiUsageDashboard.CodexCli.{currentUserSid}");
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
