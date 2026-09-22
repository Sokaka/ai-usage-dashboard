using System.IO;
using System.Reflection;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App;

internal sealed record AppUpdateBuildDefaults(
	Uri FeedUri,
	string Channel,
	UpdateFeedTrustStore TrustedKeys)
{
	private const string ChannelMetadataName =
		"AiUsageDashboard.UpdateChannel";
	private const string FeedUrlMetadataName =
		"AiUsageDashboard.UpdateFeedUrl";
	private const string TrustedKeysResourceName =
		"AiUsageDashboard.UpdateTrustedKeys.json";

	internal static AppUpdateBuildDefaults? Load()
	{
		Assembly assembly = typeof(AppUpdateBuildDefaults).Assembly;
		IReadOnlyDictionary<string, string> metadata = assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.Where(attribute => attribute.Value is not null)
			.ToDictionary(
				attribute => attribute.Key,
				attribute => attribute.Value ?? throw new InvalidOperationException(
					$"Assembly metadata '{attribute.Key}' has no value."),
				StringComparer.Ordinal);
		metadata.TryGetValue(FeedUrlMetadataName, out string? feedUrl);
		metadata.TryGetValue(ChannelMetadataName, out string? channel);
		using Stream? trustedKeysStream = assembly.GetManifestResourceStream(
			TrustedKeysResourceName);
		string? trustedKeysJson = trustedKeysStream is null
			? null
			: ReadTrustedKeys(trustedKeysStream);
		return Parse(feedUrl, channel, trustedKeysJson);
	}

	internal static AppUpdateBuildDefaults? Parse(
		string? feedUrl,
		string? channel,
		string? trustedKeysJson)
	{
		if ((feedUrl is null) &&
			(channel is null) &&
			(trustedKeysJson is null))
		{
			return null;
		}

		if (string.IsNullOrWhiteSpace(feedUrl) ||
			!Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri? feedUri))
		{
			throw new InvalidOperationException(
				"The app build does not contain a valid update feed URL.");
		}

		try
		{
			UpdateHttpUriPolicy.EnsureAllowed(
				feedUri,
				allowInsecureLoopbackForTests: false);
		}
		catch (InvalidDataException exception)
		{
			throw new InvalidOperationException(
				"The app build does not contain a valid update feed URL.",
				exception);
		}

		if ((channel is null) || !IsValidChannel(channel))
		{
			throw new InvalidOperationException(
				"The app build does not contain a valid update channel.");
		}

		if (string.IsNullOrWhiteSpace(trustedKeysJson))
		{
			throw new InvalidOperationException(
				"The app build does not contain update feed trust keys.");
		}

		return new AppUpdateBuildDefaults(
			feedUri,
			channel,
			UpdateFeedTrustStore.Parse(trustedKeysJson));
	}

	private static bool IsValidChannel(string? channel)
	{
		if (string.IsNullOrEmpty(channel) ||
			(channel.Length > 32) ||
			(channel[0] < 'a') ||
			(channel[0] > 'z'))
		{
			return false;
		}

		return channel.Skip(1).All(character =>
			(character >= 'a' && character <= 'z') ||
			(character >= '0' && character <= '9') ||
			(character == '-'));
	}

	private static string ReadTrustedKeys(Stream stream)
	{
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}
}
