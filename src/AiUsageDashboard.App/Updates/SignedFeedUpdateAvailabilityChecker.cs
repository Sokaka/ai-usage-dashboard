using System.IO;
using System.Net.Http;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal sealed class SignedFeedUpdateAvailabilityChecker :
	IUpdateAvailabilityChecker
{
	private readonly string _channel;
	private readonly Uri _feedUri;
	private readonly SignedUpdateFeedClient _feedClient;

	internal SignedFeedUpdateAvailabilityChecker(
		SignedUpdateFeedClient feedClient,
		Uri feedUri,
		string channel)
	{
		_feedClient = feedClient ??
			throw new ArgumentNullException(nameof(feedClient));
		_feedUri = feedUri ?? throw new ArgumentNullException(nameof(feedUri));
		ArgumentException.ThrowIfNullOrWhiteSpace(channel);
		_channel = channel;
	}

	internal static SignedFeedUpdateAvailabilityChecker Create(
		HttpClient httpClient,
		AppUpdateBuildDefaults buildDefaults)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		ArgumentNullException.ThrowIfNull(buildDefaults);
		return new SignedFeedUpdateAvailabilityChecker(
			new SignedUpdateFeedClient(
				httpClient,
				buildDefaults.TrustedKeys),
			buildDefaults.FeedUri,
			buildDefaults.Channel);
	}

	public async Task<UpdateAvailabilityCheckResult> CheckAsync(
		UpdateAvailabilityCheckRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ResolvedUpdateReleaseFeed resolvedFeed =
			await _feedClient.FetchFeedAsync(
				_feedUri,
				_channel,
				cancellationToken).ConfigureAwait(false);
		UpdateManifest availableManifest =
			resolvedFeed.Feed.CreatePackageManifest();
		if (request.HighestObservedReleaseSequence is
			long highestObservedSequence &&
			(availableManifest.ReleaseSequence is not long releaseSequence ||
				releaseSequence < highestObservedSequence))
		{
			throw new InvalidDataException(
				"The signed update feed is older than the highest observed release sequence.");
		}

		AiUsageDashboard.Updater.Core.UpdateAvailabilityStatus coreStatus =
			request.InstallationContext.Kind switch
			{
				AppInstallationKind.Unmanaged =>
					UpdateAvailabilityEvaluator.EvaluateUnmanagedProductVersion(
					request.CurrentVersion,
					availableManifest),
				AppInstallationKind.CanonicalManaged or
					AppInstallationKind.CustomManaged =>
					UpdateAvailabilityEvaluator.EvaluateManagedPayload(
					request.InstallationContext.InstalledManifest ??
						throw new InvalidDataException(
							"A managed installation context has no installed manifest."),
					availableManifest),
				_ => throw new InvalidDataException(
					"The installation context has an unknown kind.")
			};
		UpdateAvailabilityStatus status = coreStatus switch
		{
			AiUsageDashboard.Updater.Core.UpdateAvailabilityStatus.UpToDate =>
				UpdateAvailabilityStatus.UpToDate,
			AiUsageDashboard.Updater.Core.UpdateAvailabilityStatus.UpdateAvailable =>
				UpdateAvailabilityStatus.UpdateAvailable,
			AiUsageDashboard.Updater.Core.UpdateAvailabilityStatus.UnknownCurrentVersion =>
				UpdateAvailabilityStatus.UnknownCurrentVersion,
			_ => throw new InvalidDataException(
				"The update availability evaluator returned an unknown state.")
		};

		return new UpdateAvailabilityCheckResult(
			status,
			availableManifest.Version,
			availableManifest.ReleaseSequence ?? throw new InvalidDataException(
				"The signed update feed has no release sequence."));
	}
}
