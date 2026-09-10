using System.Text;

using AiUsageDashboard.PrivacyCheck;

namespace AiUsageDashboard.Tests;

public sealed class PrivacyRulesTests
{
	[Theory]
	[InlineData(".env.production")]
	[InlineData(".ENV.LOCAL")]
	[InlineData(".env.example.local")]
	[InlineData("auth.backup.json")]
	[InlineData("credentials.production.json")]
	[InlineData("accounts.json.bak")]
	[InlineData("appsettings.Local.json")]
	[InlineData("private-key-backup")]
	[InlineData("private-key.json")]
	[InlineData("signing.pem")]
	[InlineData("usage-snapshot-20260101.json")]
	[InlineData("agy-reviewed.profile.json")]
	[InlineData("reviewed.private-profile.json")]
	public void IsSensitiveArtifactName_RejectsPrivateArtifactVariants(string fileName)
	{
		Assert.True(PrivacyRules.IsSensitiveArtifactName(fileName));
	}

	[Theory]
	[InlineData(".env.example")]
	[InlineData("auth.example.json")]
	[InlineData("credentials.example.json")]
	public void ExampleFileNames_AllowTemplatesWithoutExemptingTheirContents(string fileName)
	{
		string syntheticToken = "s" + "k-" + new string('A', 32);

		Assert.False(PrivacyRules.IsSensitiveArtifactName(fileName));
		Assert.True(PrivacyRules.IsKnownTextFile(fileName));
		PrivacyMatch match = Assert.Single(PrivacyRules.ScanText(syntheticToken, includePersonalData: true));
		Assert.Equal("provider API key", match.Rule);
	}

	[Theory]
	[InlineData("README.unlisted")]
	[InlineData("settings")]
	public void UnknownTextFormats_AreDecodedAndScanned(string fileName)
	{
		string syntheticToken = "g" + "hp_" + new string('B', 36);
		byte[] bytes = Encoding.UTF8.GetBytes("first line\n" + syntheticToken);

		Assert.False(PrivacyRules.IsKnownTextFile(fileName));
		Assert.True(PrivacyRules.TryDecodeText(bytes, out string text));
		PrivacyMatch match = Assert.Single(PrivacyRules.ScanText(text, includePersonalData: true));
		Assert.Equal(new PrivacyMatch(2, "GitHub access token"), match);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TryDecodeText_ReadsStrictUtf8WithOrWithoutBom(bool includeBom)
	{
		Encoding encoding = new UTF8Encoding(includeBom, true);
		const string ExpectedText = "文字範例\r\nnext line";
		byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(ExpectedText)).ToArray();

		Assert.True(PrivacyRules.TryDecodeText(bytes, out string text));
		Assert.Equal(ExpectedText, text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TryDecodeText_ReadsUtf16BomAndKeepsSecretLineNumbers(bool bigEndian)
	{
		Encoding encoding = new UnicodeEncoding(bigEndian, true, true);
		string syntheticToken = "s" + "k-" + new string('C', 32);
		string expectedText = "文件\r\n" + syntheticToken;
		byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(expectedText)).ToArray();

		Assert.True(PrivacyRules.TryDecodeText(bytes, out string text));
		Assert.Equal(expectedText, text);
		PrivacyMatch match = Assert.Single(PrivacyRules.ScanText(text, includePersonalData: true));
		Assert.Equal(new PrivacyMatch(2, "provider API key"), match);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TryDecodeText_ReadsBomlessUtf16AsciiLayout(bool bigEndian)
	{
		Encoding encoding = new UnicodeEncoding(bigEndian, false, true);
		const string ExpectedText = "first line\nsecond line";

		Assert.True(PrivacyRules.TryDecodeText(encoding.GetBytes(ExpectedText), out string text));
		Assert.Equal(ExpectedText, text);
	}

	[Fact]
	public void TryDecodeText_RejectsMalformedKnownTextInsteadOfReplacingBytes()
	{
		byte[] malformedUtf8 = { 0xc3, 0x28 };

		Assert.True(PrivacyRules.IsKnownTextFile("guide.md"));
		Assert.False(PrivacyRules.TryDecodeText(malformedUtf8, out string text));
		Assert.Empty(text);
	}

	[Fact]
	public void TryDecodeText_RejectsMalformedUtf16()
	{
		byte[] incompleteUtf16 = { 0xff, 0xfe, 0x41 };

		Assert.False(PrivacyRules.TryDecodeText(incompleteUtf16, out string text));
		Assert.Empty(text);
	}

	[Fact]
	public void TryDecodeText_RejectsBinaryControlCharacters()
	{
		byte[] bytes = { (byte)'A', 0, 0, (byte)'B', 0x01 };

		Assert.False(PrivacyRules.TryDecodeText(bytes, out string text));
		Assert.Empty(text);
	}

	[Fact]
	public void ScanText_PersonalDataExceptionStillChecksSecrets()
	{
		string authorEmail = "author" + "@" + "publisher.invalid-domain.com";
		string authorPath = "C:/Users/" + "private-person/document.md";
		string syntheticToken = "s" + "k-" + new string('D', 32);
		string content = authorEmail + "\n" + authorPath + "\n" + syntheticToken;

		IReadOnlyList<PrivacyMatch> ordinaryMatches = PrivacyRules.ScanText(content, includePersonalData: true);
		Assert.Contains(ordinaryMatches, match => match.Rule == "non-example email address");
		Assert.Contains(ordinaryMatches, match => match.Rule == "user-specific home path");
		PrivacyMatch noticeMatch = Assert.Single(PrivacyRules.ScanText(content, includePersonalData: false));
		Assert.Equal(new PrivacyMatch(3, "provider API key"), noticeMatch);
	}

	[Fact]
	public void ScanText_ReportsOnlyLineNumbersAndRules()
	{
		string syntheticToken = "g" + "hp_" + new string('E', 36);
		IReadOnlyList<PrivacyMatch> matches = PrivacyRules.ScanText(syntheticToken, includePersonalData: true);

		Assert.Single(matches);
		Assert.DoesNotContain(syntheticToken, string.Join("\n", matches));
		Assert.Equal(new[] { "LineNumber", "Rule" },
			typeof(PrivacyMatch).GetProperties().Select(property => property.Name).Order().ToArray());
	}

	[Fact]
	public void ScanText_PreservesExistingHighConfidenceSecretFamilies()
	{
		string[] syntheticSecrets =
		{
			"-----" + "BEGIN PRIVATE KEY" + "-----",
			"s" + "k-" + new string('F', 32),
			"g" + "hp_" + new string('G', 36),
			"A" + "KIA" + new string('H', 16),
			"A" + "Iza" + new string('I', 35),
			"x" + "oxb-" + new string('J', 24),
			"https://" + "example-user:example-password@api.example.com"
		};
		string[] expectedRules =
		{
			"private-key material", "provider API key", "GitHub access token",
			"AWS access key", "Google API key", "Slack access token", "credential embedded in URL"
		};

		IReadOnlyList<PrivacyMatch> matches = PrivacyRules.ScanText(
			string.Join("\n", syntheticSecrets), includePersonalData: false);

		Assert.Equal(expectedRules, matches.Select(match => match.Rule).ToArray());
		Assert.Equal(Enumerable.Range(1, syntheticSecrets.Length), matches.Select(match => match.LineNumber));
	}

	[Fact]
	public void ScanText_PlaceholderDoesNotHideAnotherCredentialOnTheSameLine()
	{
		string placeholder = "password=" + "example-placeholder-password-value";
		string syntheticAssignment = " password=" + "Abcdef0123456789GhijklMNO";

		PrivacyMatch match = Assert.Single(PrivacyRules.ScanText(
			placeholder + syntheticAssignment, includePersonalData: true));
		Assert.Equal("credential-like assignment", match.Rule);
	}

	[Fact]
	public void ScanText_ObservesCancellationForTextAndEmptyInput()
	{
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		Assert.Throws<OperationCanceledException>(() => PrivacyRules.ScanText(
			"plain text", includePersonalData: true, cancellation.Token));
		Assert.Throws<OperationCanceledException>(() => PrivacyRules.ScanText(
			string.Empty, includePersonalData: true, cancellation.Token));
	}

	[Fact]
	public void ScanText_RejectsExcessMatchesWithoutDisclosingTheirValues()
	{
		string syntheticToken = "s" + "k-" + new string('K', 32);
		string limitContent = string.Join("\n", Enumerable.Repeat(syntheticToken, 10000));
		Assert.Equal(10000, PrivacyRules.ScanText(limitContent, includePersonalData: false).Count);

		PrivacyCheckException exception = Assert.Throws<PrivacyCheckException>(() => PrivacyRules.ScanText(
			limitContent + "\n" + syntheticToken, includePersonalData: false));

		Assert.Contains("10000 matches", exception.Message);
		Assert.Contains("line 10001", exception.Message);
		Assert.DoesNotContain(syntheticToken, exception.Message);
	}

	[Fact]
	public void ScanText_AllowsDocumentedPlaceholders()
	{
		string content = "user@example.com\nC:/Users/test/readme.md\npassword=" +
			"example-placeholder-password-value";

		Assert.Empty(PrivacyRules.ScanText(content, includePersonalData: true));
	}
}
