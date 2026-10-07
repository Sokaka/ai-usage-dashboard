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
	private readonly WindowsOfficialCliExecutableStager _stager;

	internal static IGrokCliExecutableStager Shared { get; } =
		new GrokCliExecutableStager();

	internal GrokCliExecutableStager()
		: this(
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			WindowsExecutablePathSecurity.IsPathAclSafe)
	{
	}

	internal GrokCliExecutableStager(
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
				"無法鎖定或驗證 Grok Build CLI 的官方原始執行檔。");
		}
	}

	public void Dispose()
	{
		_stager.Dispose();
	}

}
