using AiUsageDashboard.App;

namespace AiUsageDashboard.Tests;

public sealed class AppUpdateBuildDefaultsTests :
	IClassFixture<FeedSigningTestKeys>
{
	private readonly FeedSigningTestKeys _signingKeys;

	public AppUpdateBuildDefaultsTests(FeedSigningTestKeys signingKeys)
	{
		_signingKeys = signingKeys;
	}

	[Fact]
	public void Load_FromDevelopmentBuildWithoutMetadata_ReturnsUnavailable()
	{
		Assert.Null(AppUpdateBuildDefaults.Load());
	}

	[Fact]
	public void Parse_WithCompleteMetadata_ReturnsValidatedDefaults()
	{
		AppUpdateBuildDefaults defaults = Assert.IsType<AppUpdateBuildDefaults>(
			AppUpdateBuildDefaults.Parse(
				"https://updates.example.test/stable.json",
				"stable",
				_signingKeys.TrustJson("test-first")));

		Assert.Equal(
			new Uri("https://updates.example.test/stable.json"),
			defaults.FeedUri);
		Assert.Equal("stable", defaults.Channel);
		Assert.NotNull(defaults.TrustedKeys);
	}

	[Theory]
	[InlineData("http://updates.example.test/stable.json")]
	[InlineData("https:///stable.json")]
	[InlineData("https://user@updates.example.test/stable.json")]
	[InlineData("https://updates.example.test/stable.json#fragment")]
	[InlineData("relative.json")]
	public void Parse_WithUnsafeFeedUrl_Rejects(string feedUrl)
	{
		Assert.Throws<InvalidOperationException>(() =>
			AppUpdateBuildDefaults.Parse(
				feedUrl,
				"stable",
				_signingKeys.TrustJson("test-first")));
	}

	[Theory]
	[InlineData("")]
	[InlineData("Stable")]
	[InlineData("stable_channel")]
	[InlineData("stable-channel-that-is-longer-than-32")]
	public void Parse_WithInvalidChannel_Rejects(string channel)
	{
		Assert.Throws<InvalidOperationException>(() =>
			AppUpdateBuildDefaults.Parse(
				"https://updates.example.test/stable.json",
				channel,
				_signingKeys.TrustJson("test-first")));
	}

	[Fact]
	public void Parse_WithPartialMetadata_Rejects()
	{
		Assert.Throws<InvalidOperationException>(() =>
			AppUpdateBuildDefaults.Parse(
				"https://updates.example.test/stable.json",
				"stable",
				trustedKeysJson: null));
	}

	[Fact]
	public void Parse_WithMalformedTrustStore_Rejects()
	{
		Assert.Throws<InvalidDataException>(() =>
			AppUpdateBuildDefaults.Parse(
				"https://updates.example.test/stable.json",
				"stable",
				"{}"));
	}
}
