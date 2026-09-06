using System.Reflection;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record UpdaterBuildDefaults(Uri FeedUri, string Channel)
{
	private const string ChannelMetadataName =
		"AiUsageDashboard.UpdateChannel";
	private const string FeedUrlMetadataName =
		"AiUsageDashboard.UpdateFeedUrl";
	private const string TrustedKeysResourceName =
		"AiUsageDashboard.UpdateTrustedKeys.json";

	internal static UpdateFeedTrustStore LoadTrustedKeys()
	{
		using Stream stream = typeof(Program).Assembly.GetManifestResourceStream(TrustedKeysResourceName) ??
			throw new InvalidDataException("This updater build has no embedded update feed trust keys; online updates are unavailable.");
		using StreamReader reader = new(stream);
		return UpdateFeedTrustStore.Parse(reader.ReadToEnd());
	}

	internal static UpdaterBuildDefaults Load()
	{
		IReadOnlyDictionary<string, string> metadata = typeof(Program).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.Where(attribute => attribute.Value is not null)
			.ToDictionary(
				attribute => attribute.Key,
				attribute => attribute.Value!,
				StringComparer.Ordinal);

		if (!metadata.TryGetValue(FeedUrlMetadataName, out string? feedUrl) ||
			!Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri? feedUri) ||
			(feedUri.Scheme != Uri.UriSchemeHttps) ||
			!string.IsNullOrEmpty(feedUri.UserInfo) ||
			!string.IsNullOrEmpty(feedUri.Fragment))
		{
			throw new InvalidOperationException(
				"The updater build does not contain a valid update feed URL.");
		}

		if (!metadata.TryGetValue(ChannelMetadataName, out string? channel) ||
			string.IsNullOrWhiteSpace(channel))
		{
			throw new InvalidOperationException(
				"The updater build does not contain a valid update channel.");
		}

		return new UpdaterBuildDefaults(feedUri, channel);
	}
}
