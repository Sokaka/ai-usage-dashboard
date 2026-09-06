using System.IO;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal interface IGrokCliExecutableStager
{
	WindowsOfficialCliExecutableLease Stage(
		string sourceExecutablePath,
		CancellationToken cancellationToken = default);
}

internal sealed class GrokCliExecutableStager :
	IGrokCliExecutableStager,
	IDisposable
{
	internal const long MaximumExecutableSizeBytes =
		WindowsOfficialCliExecutableStager.MaximumExecutableSizeBytes;
	private const string ExpectedSignerName = "X.AI LLC";
	private const string StagedFilePrefix = "grok-";
	private const string TemporaryFilePrefix = ".grok-stage-";
	private readonly WindowsOfficialCliExecutableStager _stager;

	internal static IGrokCliExecutableStager Shared { get; } =
		new GrokCliExecutableStager();

	internal GrokCliExecutableStager()
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

	internal GrokCliExecutableStager(
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
		catch (WindowsOfficialCliExecutableStagingException exception) when (
			exception.FailureReason ==
				WindowsOfficialCliExecutableStagingFailureReason.SourceNotFound)
		{
			throw new GrokCliNotFoundException("找不到官方 Grok Build CLI。");
		}
		catch (WindowsOfficialCliExecutableStagingException exception) when (
			exception.FailureReason ==
				WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature)
		{
			throw new GrokCliUntrustedException(
				"Grok Build CLI 未通過 X.AI LLC 官方簽章驗證。");
		}
		catch (WindowsOfficialCliExecutableStagingException)
		{
			throw new GrokCliUntrustedException(
				"無法建立或驗證 Grok Build CLI 的受保護執行副本。");
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
			throw new GrokCliUntrustedException(
				"無法確認 Grok Build CLI 的受保護執行目錄。");
		}

		return Path.Combine(
			fixedDriveRoot,
			$"AiUsageDashboard.GrokCli.{currentUserSid}",
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
