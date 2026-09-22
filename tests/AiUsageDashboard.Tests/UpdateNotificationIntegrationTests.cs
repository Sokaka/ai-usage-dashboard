using System.Net;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App;
using AiUsageDashboard.App.Updates;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdateNotificationIntegrationTests :
	IClassFixture<FeedSigningTestKeys>
{
	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly byte[] _responseBody;

		internal int SendCount { get; private set; }

		internal StubHttpMessageHandler(byte[] responseBody)
		{
			_responseBody = responseBody;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SendCount++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(_responseBody),
				RequestMessage = request
			});
		}
	}

	private const string FeedUri =
		"https://updates.example.test/internal/latest.json";
	private const string PackageSha256 =
		"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	private static readonly DateTimeOffset TestNow = new(
		2026,
		9,
		15,
		6,
		0,
		0,
		TimeSpan.Zero);
	private readonly FeedSigningTestKeys _signingKeys;

	public UpdateNotificationIntegrationTests(FeedSigningTestKeys signingKeys)
	{
		_signingKeys = signingKeys;
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DetectCheckAndPresentAsync_WhenNonCanonicalUpdateIsAvailable_UsesReleases(
		bool isPortable)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string localApplicationData = Path.Combine(
			temporaryDirectory.Path,
			"local-app-data");
		Directory.CreateDirectory(localApplicationData);
		string executablePath = isPortable
			? CreatePortableExecutable(temporaryDirectory.Path)
			: await CreateManagedExecutableAsync(temporaryDirectory.Path);
		AppInstallationContextDetector detector = new(
			() => localApplicationData);
		AppInstallationContext context = await detector.DetectAsync(
			executablePath);
		AppInstallationKind expectedKind = isPortable
			? AppInstallationKind.Unmanaged
			: AppInstallationKind.CustomManaged;
		Assert.Equal(expectedKind, context.Kind);

		string currentProductVersion = isPortable
			? "1.0.3+portable-fixture"
			: "9.9.9+must-not-be-used";
		StubHttpMessageHandler handler = new(CreateSignedFeedBytes());
		using HttpClient httpClient = new(handler);
		SignedFeedUpdateAvailabilityChecker checker =
			SignedFeedUpdateAvailabilityChecker.Create(
				httpClient,
				new AppUpdateBuildDefaults(
					new Uri(FeedUri),
					"internal",
					_signingKeys.Trust("test-first")));
		UpdateAvailabilityCheckResult result = await checker.CheckAsync(
			new UpdateAvailabilityCheckRequest(
				context,
				currentProductVersion,
				HighestObservedReleaseSequence: null),
			CancellationToken.None);

		Assert.Equal(
			AiUsageDashboard.App.Updates.UpdateAvailabilityStatus.UpdateAvailable,
			result.Status);
		Assert.Equal("1.0.4", result.AvailableVersion);
		Assert.Equal(1018, result.ReleaseSequence);
		Assert.Equal(1, handler.SendCount);
		Assert.Equal(
			isPortable ? null : "1.0.3",
			context.InstalledManifest?.Version);

		int maintenancePathReadCount = 0;
		MaintenanceUpdaterLauncher launcher = new(
			() =>
			{
				maintenancePathReadCount++;
				throw new InvalidOperationException(
					"The maintenance path must not be read.");
			},
			_ => throw new InvalidOperationException(
				"The maintenance updater must not start."));
		bool canLaunchUpdater = launcher.CanLaunch(
			context,
			isShutdownChannelReady: true);

		Assert.False(canLaunchUpdater);
		Assert.Equal(0, maintenancePathReadCount);

		UpdatePresentationState state = new(
			UpdatePresentationStatus.UpdateAvailable,
			IsAutoCheckEnabled: true,
			HasShownAutomaticCheckNotice: true,
			CurrentVersion: "1.0.3",
			result.AvailableVersion,
			result.ReleaseSequence,
			new UpdateKnownResult(
				CheckedAgainstVersion: "1.0.3",
				result.AvailableVersion,
				result.ReleaseSequence,
				IsUpdateAvailable: true),
			LastSuccessfulCheckUtc: TestNow,
			LastFailureUtc: null,
			FailureMessage: null,
			IsManualCheck: true,
			IsSnoozed: false,
			SnoozedUntilUtc: null);
		UpdateUiPresentation presentation = UpdateUiPresentationFactory.Create(
			state,
			shouldShowAutomaticCheckNotice: false,
			context.Kind,
			canLaunchUpdater,
			isUpdaterRunning: false,
			TestNow);

		Assert.Equal(
			UpdatePrimaryActionKind.OpenReleases,
			presentation.PrimaryAction);
		Assert.True(presentation.IsPrimaryActionEnabled);
		Assert.True(presentation.IsBannerVisible);
		Assert.True(presentation.HasUpdateBadge);
		Assert.Equal("開啟下載頁", presentation.PrimaryActionText);
		Assert.Equal("開啟下載頁", presentation.TrayUpdateActionText);
		Assert.Contains(
			isPortable ? "解壓到新資料夾" : "不會原地更新",
			presentation.BannerText,
			StringComparison.Ordinal);
	}

	private byte[] CreateSignedFeedBytes()
	{
		UpdateReleaseFeed feed = new(
			UpdateReleaseFeed.CurrentSchemaVersion,
			"internal",
			ReleaseSequence: 1018,
			MinimumUpdaterVersion: "1.0.0",
			new UpdateReleaseArtifact(
				"AiUsageDashboard",
				"1.0.4",
				"win-x64",
				"AiUsageDashboard-1.0.4-win-x64.zip",
				"https://cdn.example.test/AiUsageDashboard-1.0.4-win-x64.zip",
				SizeBytes: 1234,
				PackageSha256,
				SourceRevision: "abcdef"),
			new UpdateReleaseArtifact(
				"AiUsageDashboard.Updater",
				"1.0.0",
				"win-x64",
				"AiUsageDashboard-Updater-1.0.0-win-x64.exe",
				"https://cdn.example.test/AiUsageDashboard-Updater-1.0.0-win-x64.exe",
				SizeBytes: 4321,
				"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
				SourceRevision: "abcdef"));
		string signedFeed = _signingKeys.Sign(JsonSerializer.Serialize(feed));
		return Encoding.UTF8.GetBytes(signedFeed);
	}

	private static string CreatePortableExecutable(string rootDirectory)
	{
		string portableDirectory = Path.Combine(rootDirectory, "portable");
		Directory.CreateDirectory(portableDirectory);
		string executablePath = Path.Combine(
			portableDirectory,
			"AiUsageDashboard.App.exe");
		File.WriteAllBytes(executablePath, [0]);
		return executablePath;
	}

	private static async Task<string> CreateManagedExecutableAsync(
		string rootDirectory)
	{
		string installRoot = Path.Combine(rootDirectory, "custom");
		string appDirectory = Path.Combine(installRoot, "current", "app");
		Directory.CreateDirectory(appDirectory);
		string executablePath = Path.Combine(
			appDirectory,
			"AiUsageDashboard.App.exe");
		await File.WriteAllBytesAsync(executablePath, [0]);
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.0.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			ArchiveSizeBytes: 1200,
			PackageSha256,
			SourceRevision: "installed-source",
			ReleaseSequence: 1017);
		await manifest.WriteAsync(Path.Combine(
			installRoot,
			"current",
			UpdateManifest.InstalledFileName));
		return executablePath;
	}
}
