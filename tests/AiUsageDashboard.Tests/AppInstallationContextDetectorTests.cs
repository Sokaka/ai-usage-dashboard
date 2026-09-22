using AiUsageDashboard.App.Updates;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class AppInstallationContextDetectorTests
{
	private const long ReleaseSequence = 1017;
	private static readonly string ArchiveSha256 = new('a', 64);

	[Fact]
	public async Task DetectAsync_WhenRunningCanonicalPayload_ReturnsCanonicalManaged()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string localApplicationData = temporaryDirectory.Path;
		string installRoot = Path.Combine(
			localApplicationData,
			"Programs",
			"AiUsageDashboard");
		string executablePath = await CreateManagedLayoutAsync(installRoot);
		AppInstallationContextDetector detector = new(
			() => localApplicationData);

		AppInstallationContext context = await detector.DetectAsync(
			executablePath);

		Assert.Equal(AppInstallationKind.CanonicalManaged, context.Kind);
		Assert.Equal(Path.GetFullPath(executablePath), context.ExecutablePath);
		Assert.Equal(Path.GetFullPath(installRoot), context.InstallRoot);
		Assert.Equal(
			Path.Combine(
				installRoot,
				"current",
				UpdateManifest.InstalledFileName),
			context.ManifestPath);
		Assert.Equal("1.0.3", context.InstalledManifest?.Version);
		Assert.Equal(ArchiveSha256, context.PayloadGenerationId);
	}

	[Fact]
	public async Task DetectAsync_WhenRunningExactCustomLayout_ReturnsCustomManaged()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string customRoot = Path.Combine(temporaryDirectory.Path, "custom");
		string executablePath = await CreateManagedLayoutAsync(customRoot);
		AppInstallationContextDetector detector = new(
			() => Path.Combine(temporaryDirectory.Path, "local-app-data"));

		AppInstallationContext context = await detector.DetectAsync(
			executablePath);

		Assert.Equal(AppInstallationKind.CustomManaged, context.Kind);
		Assert.Equal(Path.GetFullPath(customRoot), context.InstallRoot);
		Assert.NotNull(context.InstalledManifest);
	}

	[Fact]
	public async Task DetectAsync_WhenManifestIsMalformed_FailsClosedAndReportsDiagnostic()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "custom");
		string executablePath = CreateExecutable(installRoot);
		string manifestPath = Path.Combine(
			installRoot,
			"current",
			UpdateManifest.InstalledFileName);
		await File.WriteAllTextAsync(manifestPath, "{}");
		List<Exception?> diagnostics = new();
		AppInstallationContextDetector detector = new(
			() => temporaryDirectory.Path,
			(_, exception) => diagnostics.Add(exception));

		AppInstallationContext context = await detector.DetectAsync(
			executablePath);

		Assert.Equal(AppInstallationKind.Unmanaged, context.Kind);
		Assert.Single(diagnostics);
		Assert.IsType<InvalidDataException>(diagnostics[0]);
	}

	[Theory]
	[InlineData("portable", true)]
	[InlineData("copied", false)]
	public async Task DetectAsync_WhenLayoutOrManifestIsMissing_ReturnsUnmanaged(
		string scenario,
		bool useExpectedExecutableName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath;

		if (scenario == "portable")
		{
			executablePath = Path.Combine(
				temporaryDirectory.Path,
				useExpectedExecutableName
					? "AiUsageDashboard.App.exe"
					: "copied.exe");
			await File.WriteAllBytesAsync(executablePath, [0]);
		}
		else
		{
			string installRoot = Path.Combine(temporaryDirectory.Path, "custom");
			executablePath = CreateExecutable(installRoot);
		}

		AppInstallationContextDetector detector = new(
			() => temporaryDirectory.Path);

		AppInstallationContext context = await detector.DetectAsync(
			executablePath);

		Assert.Equal(AppInstallationKind.Unmanaged, context.Kind);
		Assert.Null(context.InstallRoot);
		Assert.Null(context.InstalledManifest);
	}

	[Fact]
	public async Task DetectAsync_WhenCancellationIsRequested_PropagatesCancellation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = await CreateManagedLayoutAsync(
			Path.Combine(temporaryDirectory.Path, "custom"));
		AppInstallationContextDetector detector = new(
			() => temporaryDirectory.Path);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			detector.DetectAsync(executablePath, cancellation.Token));
	}

	[Theory]
	[InlineData("1.0.4+abcdef", true, "1.0.4")]
	[InlineData("1.0.4-beta.1+abcdef", true, "1.0.4-beta.1")]
	[InlineData("1.0", false, "")]
	[InlineData("1.0.4+", false, "")]
	[InlineData(null, false, "")]
	public void CurrentVersionParser_NormalizesSemVerCore(
		string? value,
		bool expectedResult,
		string expectedVersion)
	{
		bool result = AppCurrentVersionParser.TryParse(
			value,
			out string normalizedVersion);

		Assert.Equal(expectedResult, result);
		Assert.Equal(expectedVersion, normalizedVersion);
	}

	private static string CreateExecutable(string installRoot)
	{
		string appDirectory = Path.Combine(installRoot, "current", "app");
		Directory.CreateDirectory(appDirectory);
		string executablePath = Path.Combine(
			appDirectory,
			"AiUsageDashboard.App.exe");
		File.WriteAllBytes(executablePath, [0]);
		return executablePath;
	}

	private static async Task<string> CreateManagedLayoutAsync(
		string installRoot)
	{
		string executablePath = CreateExecutable(installRoot);
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.0.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			ArchiveSizeBytes: 1234,
			ArchiveSha256,
			SourceRevision: "abcdef",
			ReleaseSequence);
		await manifest.WriteAsync(Path.Combine(
			installRoot,
			"current",
			UpdateManifest.InstalledFileName));
		return executablePath;
	}
}
