namespace AiUsageDashboard.Updater.Core;

public static class UpdateFeedValidator
{
	public static void Validate(string json, string expectedChannel)
	{
		UpdateReleaseFeed feed = UpdateReleaseFeed.Parse(json);
		feed.ValidateExpectedChannel(expectedChannel);
	}
}
