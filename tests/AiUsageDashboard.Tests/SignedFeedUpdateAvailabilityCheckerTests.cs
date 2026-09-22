using System.Net;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App;
using AiUsageDashboard.App.Updates;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class SignedFeedUpdateAvailabilityCheckerTests :
	IClassFixture<FeedSigningTestKeys>
{
	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly byte[] _responseBody;

		internal StubHttpMessageHandler(byte[] responseBody)
		{
			_responseBody = responseBody;
		}

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
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
	private const long PackageSizeBytes = 1234;
	private readonly FeedSigningTestKeys _signingKeys;

	public SignedFeedUpdateAvailabilityCheckerTests(
		FeedSigningTestKeys signingKeys)
	{
		_signingKeys = signingKeys;
	}

	[Fact]
	public async Task CheckAsync_WhenFeedSequenceRollsBackBelowPersistedHighest_Rejects()
	{
		using HttpClient httpClient = CreateHttpClient(
			CreateFeedBytes("1.0.4", releaseSequence: 42));
		SignedFeedUpdateAvailabilityChecker checker = CreateChecker(httpClient);
		UpdateAvailabilityCheckRequest request = new(
			AppInstallationContext.CreateUnmanaged(
				"C:\\portable\\AiUsageDashboard.App.exe"),
			CurrentVersion: "1.0.3",
			HighestObservedReleaseSequence: 43);

		InvalidDataException exception =
			await Assert.ThrowsAsync<InvalidDataException>(() =>
				checker.CheckAsync(request, CancellationToken.None));

		Assert.Contains(
			"older than the highest observed release sequence",
			exception.Message,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("1.0.3", "UpdateAvailable")]
	[InlineData("1.0.4", "UpToDate")]
	public async Task CheckAsync_WithManagedContext_UsesInstalledManifest(
		string installedVersion,
		string expectedStatus)
	{
		using HttpClient httpClient = CreateHttpClient(
			CreateFeedBytes("1.0.4", releaseSequence: 42));
		SignedFeedUpdateAvailabilityChecker checker = CreateChecker(httpClient);
		UpdateManifest installedManifest = CreateManifest(
			installedVersion,
			releaseSequence: 41);
		AppInstallationContext context = new(
			AppInstallationKind.CustomManaged,
			"C:\\custom\\current\\app\\AiUsageDashboard.App.exe",
			"C:\\custom",
			"C:\\custom\\current\\installed-manifest.json",
			installedManifest);

		UpdateAvailabilityCheckResult result = await checker.CheckAsync(
			new UpdateAvailabilityCheckRequest(
				context,
				CurrentVersion: "9.9.9",
				HighestObservedReleaseSequence: null),
			CancellationToken.None);

		Assert.Equal(expectedStatus, result.Status.ToString());
		Assert.Equal("1.0.4", result.AvailableVersion);
		Assert.Equal(42, result.ReleaseSequence);
	}

	[Theory]
	[InlineData(
		"1.0.3+abcdef",
		"UpdateAvailable")]
	[InlineData("1.0.4+abcdef", "UpToDate")]
	[InlineData("malformed", "UnknownCurrentVersion")]
	[InlineData(null, "UnknownCurrentVersion")]
	public async Task CheckAsync_WithUnmanagedContext_UsesProductVersion(
		string? currentProductVersion,
		string expectedStatus)
	{
		using HttpClient httpClient = CreateHttpClient(
			CreateFeedBytes("1.0.4", releaseSequence: 42));
		SignedFeedUpdateAvailabilityChecker checker = CreateChecker(httpClient);

		UpdateAvailabilityCheckResult result = await checker.CheckAsync(
			new UpdateAvailabilityCheckRequest(
				AppInstallationContext.CreateUnmanaged(
					"C:\\portable\\AiUsageDashboard.App.exe"),
				currentProductVersion,
				HighestObservedReleaseSequence: null),
			CancellationToken.None);

		Assert.Equal(expectedStatus, result.Status.ToString());
		Assert.Equal("1.0.4", result.AvailableVersion);
		Assert.Equal(42, result.ReleaseSequence);
	}

	private static UpdateManifest CreateManifest(
		string version,
		long releaseSequence)
	{
		return new UpdateManifest(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			version,
			UpdateManifest.ExpectedRuntimeIdentifier,
			PackageSizeBytes,
			PackageSha256,
			SourceRevision: "abcdef",
			releaseSequence);
	}

	private HttpClient CreateHttpClient(byte[] responseBody)
	{
		return new HttpClient(new StubHttpMessageHandler(responseBody));
	}

	private SignedFeedUpdateAvailabilityChecker CreateChecker(
		HttpClient httpClient)
	{
		return SignedFeedUpdateAvailabilityChecker.Create(
			httpClient,
			new AppUpdateBuildDefaults(
				new Uri(FeedUri),
				"internal",
				_signingKeys.Trust("test-first")));
	}

	private byte[] CreateFeedBytes(
		string packageVersion,
		long releaseSequence)
	{
		UpdateReleaseFeed feed = new(
			UpdateReleaseFeed.CurrentSchemaVersion,
			"internal",
			releaseSequence,
			MinimumUpdaterVersion: "1.0.0",
			new UpdateReleaseArtifact(
				"AiUsageDashboard",
				packageVersion,
				"win-x64",
				$"AiUsageDashboard-{packageVersion}-win-x64.zip",
				$"https://cdn.example.test/AiUsageDashboard-{packageVersion}-win-x64.zip",
				PackageSizeBytes,
				PackageSha256,
				"abcdef"),
			new UpdateReleaseArtifact(
				"AiUsageDashboard.Updater",
				"1.0.0",
				"win-x64",
				"AiUsageDashboard-Updater-1.0.0-win-x64.exe",
				"https://cdn.example.test/AiUsageDashboard-Updater-1.0.0-win-x64.exe",
				SizeBytes: 4321,
				"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
				"abcdef"));
		string signedFeed = _signingKeys.Sign(JsonSerializer.Serialize(feed));
		return Encoding.UTF8.GetBytes(signedFeed);
	}
}
