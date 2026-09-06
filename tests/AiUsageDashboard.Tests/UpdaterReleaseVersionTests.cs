using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterReleaseVersionTests
{
	[Theory]
	[InlineData("0.0.0")]
	[InlineData("1.2.3")]
	[InlineData("1.2.3-preview.4")]
	[InlineData("10.20.30-Alpha-1.beta")]
	public void Parse_WithValidSemVer_RoundTrips(string value)
	{
		ReleaseVersion version = ReleaseVersion.Parse(value);

		Assert.Equal(value, version.ToString());
		Assert.True(ReleaseVersion.TryParse(value, out ReleaseVersion parsed));
		Assert.Equal(version, parsed);
	}

	[Theory]
	[InlineData("")]
	[InlineData("1")]
	[InlineData("1.2")]
	[InlineData("01.2.3")]
	[InlineData("1.02.3")]
	[InlineData("1.2.03")]
	[InlineData("1.2.3-")]
	[InlineData("1.2.3-alpha..1")]
	[InlineData("1.2.3-alpha_1")]
	[InlineData("1.2.3-01")]
	[InlineData("1.2.3+build.1")]
	public void TryParse_WithInvalidSemVer_Rejects(string value)
	{
		Assert.False(ReleaseVersion.TryParse(value, out _));
		Assert.Throws<FormatException>(() => ReleaseVersion.Parse(value));
	}

	[Fact]
	public void CompareTo_UsesSemVerPrecedence()
	{
		string[] orderedVersions =
		[
			"1.0.0-alpha",
			"1.0.0-alpha.1",
			"1.0.0-alpha.beta",
			"1.0.0-beta",
			"1.0.0-beta.2",
			"1.0.0-beta.11",
			"1.0.0-rc.1",
			"1.0.0",
			"2.0.0"
		];

		for (int index = 1; index < orderedVersions.Length; index++)
		{
			ReleaseVersion previous = ReleaseVersion.Parse(
				orderedVersions[index - 1]);
			ReleaseVersion current = ReleaseVersion.Parse(
				orderedVersions[index]);

			Assert.True(previous.CompareTo(current) < 0);
			Assert.True(current.CompareTo(previous) > 0);
		}
	}

	[Fact]
	public void CompareTo_WithLargeNumericIdentifiers_UsesNumericOrder()
	{
		ReleaseVersion lower = ReleaseVersion.Parse(
			"999999999999999999999999.0.0-beta.999999999999999999999999");
		ReleaseVersion higher = ReleaseVersion.Parse(
			"1000000000000000000000000.0.0-beta.1000000000000000000000000");

		Assert.True(lower.CompareTo(higher) < 0);
	}

	[Fact]
	public void CompareTo_WithWorkflowCandidateIdentifiers_UsesRunThenAttemptOrder()
	{
		ReleaseVersion firstRun = ReleaseVersion.Parse(
			"1.0.0-rc.1.run.99.attempt.8.gffffffffffff");
		ReleaseVersion secondRun = ReleaseVersion.Parse(
			"1.0.0-rc.1.run.100.attempt.1.g000000000000");
		ReleaseVersion secondAttempt = ReleaseVersion.Parse(
			"1.0.0-rc.1.run.100.attempt.2.g111111111111");

		Assert.True(firstRun.CompareTo(secondRun) < 0);
		Assert.True(secondRun.CompareTo(secondAttempt) < 0);
	}
}
