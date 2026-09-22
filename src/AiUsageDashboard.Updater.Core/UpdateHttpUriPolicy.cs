namespace AiUsageDashboard.Updater.Core;

internal static class UpdateHttpUriPolicy
{
	internal static void EnsureAllowed(
		Uri uri,
		bool allowInsecureLoopbackForTests)
	{
		ArgumentNullException.ThrowIfNull(uri);

		if (!uri.IsAbsoluteUri ||
			string.IsNullOrEmpty(uri.Host) ||
			!string.IsNullOrEmpty(uri.UserInfo) ||
			!string.IsNullOrEmpty(uri.Fragment))
		{
			throw new InvalidDataException(
				"Update URI must be an absolute server URI without userinfo or a fragment.");
		}

		if (string.Equals(
				uri.Scheme,
				Uri.UriSchemeHttps,
				StringComparison.Ordinal))
		{
			return;
		}

		if (allowInsecureLoopbackForTests &&
			string.Equals(
				uri.Scheme,
				Uri.UriSchemeHttp,
				StringComparison.Ordinal) &&
			uri.IsLoopback)
		{
			return;
		}

		throw new InvalidDataException(
			"Update URI must use HTTPS. HTTP is permitted only for loopback tests.");
	}
}
