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
	private readonly WindowsOfficialCliExecutableStager _stager;

	internal static ICodexCliExecutableStager Shared { get; } =
		new CodexCliExecutableStager();

	internal CodexCliExecutableStager()
		: this(
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			CodexOfficialExecutablePathResolver
				.IsResolvedExecutablePathAclSafe)
	{
	}

	internal CodexCliExecutableStager(
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isFixedDrivePath,
		Func<string, bool> isPathAclSafe)
	{
		_stager = new WindowsOfficialCliExecutableStager(
			ExpectedSignerName,
			inspectSignature,
			isCanonicalNonReparseFile,
			isFixedDrivePath,
			isPathAclSafe);
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
				"無法鎖定或驗證 Codex CLI 的官方原始執行檔。",
				exception);
		}
	}

	public void Dispose()
	{
		_stager.Dispose();
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

}
