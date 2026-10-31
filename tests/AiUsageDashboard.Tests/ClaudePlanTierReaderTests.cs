using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class ClaudePlanTierReaderTests
{
	[Theory]
	[InlineData("default_claude_max_5x", "max")]
	[InlineData("default_claude_max_20x", "ultra")]
	[InlineData("default_claude_max_future", "max")]
	[InlineData("", "max")]
	public void Parse_WithVerifiedMatchingMaxAccount_RefinesOnlyKnown20xTier(
		string rateLimitTier,
		string expectedPlanTier)
	{
		string json = CreateMetadataJson(rateLimitTier: rateLimitTier);

		string planTier = ClaudePlanTierReader.Parse(json, CreateContext());

		Assert.Equal(expectedPlanTier, planTier);
	}

	[Theory]
	[InlineData("pro")]
	[InlineData("team")]
	[InlineData("enterprise")]
	[InlineData("ultra")]
	public void Parse_WithNonMaxContext_PreservesVerifiedPlan(string planTier)
	{
		string json = CreateMetadataJson();

		Assert.Equal(
			planTier,
			ClaudePlanTierReader.Parse(json, CreateContext(planTier)));
	}

	[Theory]
	[InlineData(SubscriptionVerificationState.Unverified)]
	[InlineData(SubscriptionVerificationState.TransientProbeError)]
	[InlineData(SubscriptionVerificationState.DefiniteScopeMismatch)]
	[InlineData(SubscriptionVerificationState.UsageUnavailable)]
	public void Parse_WithUnverifiedContext_DoesNotInferUltra(
		SubscriptionVerificationState verificationState)
	{
		ClaudeSubscriptionContext context = CreateContext() with
		{
			VerificationState = verificationState
		};

		Assert.Equal("max", ClaudePlanTierReader.Parse(CreateMetadataJson(), context));
	}

	[Theory]
	[InlineData("other@example.com", "org-personal", "claude_max")]
	[InlineData("person@example.com", "org-company", "claude_max")]
	[InlineData("person@example.com", "ORG-PERSONAL", "claude_max")]
	[InlineData("person@example.com", "org-personal", "claude_team")]
	[InlineData("person@example.com", "org-personal", "")]
	public void Parse_WithDifferentAccountScopeOrOrganizationType_DoesNotInferUltra(
		string emailAddress,
		string organizationUuid,
		string organizationType)
	{
		string json = CreateMetadataJson(
			emailAddress: emailAddress,
			organizationUuid: organizationUuid,
			organizationType: organizationType);

		Assert.Equal("max", ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Fact]
	public void Parse_WithNormalizedMatchingIdentity_RecognizesUltra()
	{
		string json = CreateMetadataJson(
			emailAddress: " Person@Example.com ",
			organizationUuid: " org-personal ");

		Assert.Equal("ultra", ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("[]")]
	[InlineData("null")]
	[InlineData("{\"oauthAccount\":null}")]
	[InlineData("{\"oauthAccount\":[]}")]
	[InlineData("{\"oauthAccount\":{}}")]
	public void Parse_WithMissingOrInvalidMetadata_PreservesCoarsePlan(string json)
	{
		Assert.Equal("max", ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Theory]
	[InlineData("emailAddress", "null")]
	[InlineData("emailAddress", "123")]
	[InlineData("emailAddress", "\"\"")]
	[InlineData("organizationUuid", "null")]
	[InlineData("organizationUuid", "true")]
	[InlineData("organizationType", "{}")]
	[InlineData("organizationRateLimitTier", "[]")]
	public void Parse_WithInvalidMetadataField_PreservesCoarsePlan(
		string propertyName,
		string valueJson)
	{
		using JsonDocument document = JsonDocument.Parse(CreateMetadataJson());
		Dictionary<string, JsonElement> metadata = document.RootElement
			.GetProperty("oauthAccount")
			.EnumerateObject()
			.ToDictionary(property => property.Name, property => property.Value);
		using JsonDocument valueDocument = JsonDocument.Parse(valueJson);
		metadata[propertyName] = valueDocument.RootElement;
		string json = JsonSerializer.Serialize(new { oauthAccount = metadata });

		Assert.Equal("max", ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Theory]
	[InlineData("emailAddress")]
	[InlineData("organizationUuid")]
	[InlineData("organizationType")]
	[InlineData("organizationRateLimitTier")]
	public void Parse_WithMissingMetadataField_PreservesCoarsePlan(string propertyName)
	{
		using JsonDocument document = JsonDocument.Parse(CreateMetadataJson());
		Dictionary<string, JsonElement> metadata = document.RootElement
			.GetProperty("oauthAccount")
			.EnumerateObject()
			.Where(property => property.Name != propertyName)
			.ToDictionary(property => property.Name, property => property.Value);
		string json = JsonSerializer.Serialize(new { oauthAccount = metadata });

		Assert.Equal("max", ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Fact]
	public void Parse_WithDuplicateOAuthAccount_RejectsConflictingMetadata()
	{
		string json = CreateMetadataJson().Replace(
			"{\"oauthAccount\":",
			"{\"oauthAccount\":{},\"oauthAccount\":",
			StringComparison.Ordinal);

		Assert.Throws<JsonException>(() =>
			ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Theory]
	[InlineData("emailAddress", "\"other@example.com\"")]
	[InlineData("organizationUuid", "\"org-company\"")]
	[InlineData("organizationType", "\"claude_team\"")]
	[InlineData("organizationRateLimitTier", "\"default_claude_max_5x\"")]
	[InlineData("organizationRateLimitTier", "\"default_claude_max_20x\"")]
	public void Parse_WithDuplicateMetadataField_RejectsEvenMatchingValues(
		string propertyName,
		string duplicateValueJson)
	{
		string json = CreateMetadataJson().Replace(
			"{\"oauthAccount\":{",
			$"{{\"oauthAccount\":{{\"{propertyName}\":{duplicateValueJson},",
			StringComparison.Ordinal);

		Assert.Throws<JsonException>(() =>
			ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Fact]
	public void Parse_WithUnrelatedNewAndDuplicateConfigurationFields_RecognizesUltra()
	{
		string json = CreateMetadataJson().Replace(
			"{\"oauthAccount\":{",
			"{\"newSetting\":1,\"newSetting\":2,\"oauthAccount\":{\"newMetadata\":{},",
			StringComparison.Ordinal);

		Assert.Equal("ultra", ClaudePlanTierReader.Parse(json, CreateContext()));
	}

	[Fact]
	public void Read_WithMatchingSyntheticConfiguration_RecognizesUltra()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, ".claude.json");
		File.WriteAllText(filePath, CreateMetadataJson(), new UTF8Encoding(false));

		Assert.Equal(
			"ultra",
			ClaudePlanTierReader.Read(temporaryDirectory.Path, CreateContext()));
	}

	[Fact]
	public void Read_WithMissingConfiguration_DoesNotReadCredentialsOrCreateFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string credentialFilePath = Path.Combine(
			temporaryDirectory.Path,
			".credentials.json");
		File.WriteAllText(credentialFilePath, CreateMetadataJson());

		Assert.Equal(
			"max",
			ClaudePlanTierReader.Read(temporaryDirectory.Path, CreateContext()));
		Assert.Equal([credentialFilePath], Directory.GetFiles(temporaryDirectory.Path));
	}

	[Fact]
	public void Read_WithMissingDirectory_PreservesCoarsePlanWithoutCreatingIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string configDirectory = Path.Combine(temporaryDirectory.Path, "missing");

		Assert.Equal("max", ClaudePlanTierReader.Read(configDirectory, CreateContext()));
		Assert.False(Directory.Exists(configDirectory));
	}

	[Fact]
	public void Read_WithOversizeConfiguration_ReportsPathAndPreservesCause()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, ".claude.json");
		using (FileStream stream = File.Create(filePath))
		{
			stream.SetLength(ClaudePlanTierReader.MaximumFileSizeBytes + 1);
		}

		IOException exception = Assert.Throws<IOException>(() =>
			ClaudePlanTierReader.Read(temporaryDirectory.Path, CreateContext()));

		Assert.Contains(filePath, exception.Message, StringComparison.Ordinal);
		Assert.IsType<InvalidDataException>(exception.InnerException);
	}

	[Fact]
	public void Read_WithConfigurationAtMaximumSize_RecognizesUltra()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, ".claude.json");
		File.WriteAllText(
			filePath,
			CreateMetadataJson().PadRight(ClaudePlanTierReader.MaximumFileSizeBytes),
			new UTF8Encoding(false));

		Assert.Equal(ClaudePlanTierReader.MaximumFileSizeBytes, new FileInfo(filePath).Length);
		Assert.Equal(
			"ultra",
			ClaudePlanTierReader.Read(temporaryDirectory.Path, CreateContext()));
	}

	[Fact]
	public void Read_WithDuplicateMetadata_ReportsPathAndPreservesCause()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, ".claude.json");
		File.WriteAllText(filePath, "{\"oauthAccount\":{},\"oauthAccount\":{}}");

		IOException exception = Assert.Throws<IOException>(() =>
			ClaudePlanTierReader.Read(temporaryDirectory.Path, CreateContext()));

		Assert.Contains(filePath, exception.Message, StringComparison.Ordinal);
		Assert.IsAssignableFrom<JsonException>(exception.InnerException);
	}

	[Fact]
	public void Read_WithInvalidJson_ReportsPathAndPreservesCause()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(temporaryDirectory.Path, ".claude.json");
		File.WriteAllText(filePath, "{broken");

		IOException exception = Assert.Throws<IOException>(() =>
			ClaudePlanTierReader.Read(temporaryDirectory.Path, CreateContext()));

		Assert.Contains(filePath, exception.Message, StringComparison.Ordinal);
		Assert.IsAssignableFrom<JsonException>(exception.InnerException);
	}

	[Fact]
	public void Read_WithNonMaxContext_DoesNotReadInvalidConfiguration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		File.WriteAllText(Path.Combine(temporaryDirectory.Path, ".claude.json"), "{broken");

		Assert.Equal(
			"team",
			ClaudePlanTierReader.Read(temporaryDirectory.Path, CreateContext("team")));
	}

	[Fact]
	public async Task Read_WithJunctionConfigurationDirectory_RejectsWithoutReadingTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetDirectory = Path.Combine(temporaryDirectory.Path, "target");
		string junctionDirectory = Path.Combine(temporaryDirectory.Path, "junction");
		Directory.CreateDirectory(targetDirectory);
		string filePath = Path.Combine(targetDirectory, ".claude.json");
		string json = CreateMetadataJson();
		await File.WriteAllTextAsync(filePath, json);
		await JunctionTestHelper.CreateAsync(junctionDirectory, targetDirectory);
		try
		{
			IOException exception = Assert.Throws<IOException>(() =>
				ClaudePlanTierReader.Read(junctionDirectory, CreateContext()));

			Assert.Contains(
				Path.Combine(junctionDirectory, ".claude.json"),
				exception.Message,
				StringComparison.Ordinal);
			Assert.IsType<IOException>(exception.InnerException);
			Assert.Equal(json, await File.ReadAllTextAsync(filePath));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionDirectory);
		}
	}

	private static ClaudeSubscriptionContext CreateContext(string planTier = "max")
	{
		return ClaudeSubscriptionContext.CreateVerified(
			"person@example.com",
			"org-personal",
			planTier,
			"Synthetic account");
	}

	private static string CreateMetadataJson(
		string rateLimitTier = "default_claude_max_20x",
		string emailAddress = "person@example.com",
		string organizationUuid = "org-personal",
		string organizationType = "claude_max")
	{
		return JsonSerializer.Serialize(new
		{
			oauthAccount = new
			{
				emailAddress,
				organizationUuid,
				organizationType,
				organizationRateLimitTier = rateLimitTier
			}
		});
	}
}
