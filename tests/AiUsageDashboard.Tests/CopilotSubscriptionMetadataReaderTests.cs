using System.Text.Json;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CopilotSubscriptionMetadataReaderTests
{
	private const string ResponseJson = """
		{"authErrors":[],"authInfo":{"type":"token","host":"github.com","login":"synthetic-user","copilotUser":{"login":"synthetic-user","copilot_plan":"individual","access_type_sku":"paid","token_based_billing":false,"quota_snapshots":{"premium_interactions":{"token_based_billing":true}}}}}
		""";

	[Theory]
	[InlineData("")]
	[InlineData(",\"token\":\"synthetic-private-token\"")]
	[InlineData(",\"token\":null")]
	[InlineData(",\"token\":{\"auxiliary\":true}")]
	public void Read_WithOldOrRedactedToken_ReturnsEquivalentSubscription(string tokenField)
	{
		string json = ResponseJson.Replace(
			"\"host\":\"github.com\"",
			$"\"host\":\"github.com\"{tokenField}",
			StringComparison.Ordinal);

		CopilotSdkSubscriptionMetadata metadata = Assert.IsType<CopilotSdkSubscriptionMetadata>(Read(json));

		Assert.Equal(new CopilotSdkSubscriptionMetadata("individual", true), metadata);
		Assert.DoesNotContain("synthetic-private-token", metadata.ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("hmac")]
	[InlineData("env")]
	[InlineData("token")]
	[InlineData("copilot-api-token")]
	[InlineData("user")]
	[InlineData("gh-cli")]
	[InlineData("api-key")]
	public void Read_WithSupportedAuthenticationType_PreservesSubscription(string authType)
	{
		string json = ResponseJson.Replace("\"type\":\"token\"", $"\"type\":\"{authType}\"", StringComparison.Ordinal);

		Assert.Equal(new CopilotSdkSubscriptionMetadata("individual", true), Read(json));
	}

	[Theory]
	[InlineData("user")]
	[InlineData("gh-cli")]
	public void Read_WhenRequiredAuthLoginIsMissing_RejectsMetadata(string authType)
	{
		JsonElement response = JsonSerializer.SerializeToElement(new
		{
			authInfo = new
			{
				type = authType, host = "github.com",
				copilotUser = new { login = "synthetic-user", token_based_billing = true }
			}
		});

		Assert.Throws<InvalidDataException>(() =>
			CopilotSubscriptionMetadataReader.Read(response, "github.com", "synthetic-user"));
	}

	[Fact]
	public void Read_WhenOptionalEnvLoginIsMissing_UsesVerifiedCopilotUser()
	{
		JsonElement response = JsonSerializer.SerializeToElement(new
		{
			authInfo = new
			{
				type = "env", host = "github.com",
				copilotUser = new { login = "synthetic-user", token_based_billing = true }
			}
		});

		CopilotSdkSubscriptionMetadata metadata = Assert.IsType<CopilotSdkSubscriptionMetadata>(
			CopilotSubscriptionMetadataReader.Read(response, "github.com", "synthetic-user"));
		Assert.True(metadata.IsTokenBasedBilling);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"authInfo\":null}")]
	public void Read_WithoutCurrentAuthentication_ReturnsNoMetadata(string json)
	{
		Assert.Null(Read(json));
	}

	[Theory]
	[InlineData(null, "synthetic-user")]
	[InlineData("github.com", null)]
	[InlineData("github.com", " ")]
	[InlineData("other.example.test", "synthetic-user")]
	[InlineData("github.com", "different-synthetic-user")]
	public void Read_WhenExpectedIdentityDoesNotMatch_RejectsMetadata(string? expectedHost, string? expectedLogin)
	{
		using JsonDocument document = JsonDocument.Parse(ResponseJson);

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
			CopilotSubscriptionMetadataReader.Read(document.RootElement, expectedHost, expectedLogin));

		Assert.DoesNotContain("synthetic-user", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("other.example.test", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Read_WhenHostAndLoginUseEquivalentNormalization_AcceptsMetadata()
	{
		using JsonDocument document = JsonDocument.Parse(ResponseJson);

		CopilotSdkSubscriptionMetadata? metadata = CopilotSubscriptionMetadataReader.Read(
			document.RootElement,
			" https://GITHUB.COM/ ",
			" SYNTHETIC-USER ");

		Assert.Equal(new CopilotSdkSubscriptionMetadata("individual", true), metadata);
	}

	[Theory]
	[InlineData("env")]
	[InlineData("user")]
	[InlineData("gh-cli")]
	public void Read_WhenAuthenticatedLoginDoesNotMatch_RejectsMetadata(string authType)
	{
		string json = ResponseJson
			.Replace("\"type\":\"token\"", $"\"type\":\"{authType}\"", StringComparison.Ordinal)
			.Replace("\"login\":\"synthetic-user\",\"copilotUser\"", "\"login\":\"different-user\",\"copilotUser\"", StringComparison.Ordinal);

		Assert.Throws<InvalidDataException>(() => Read(json));
	}

	[Theory]
	[InlineData("hmac")]
	[InlineData("token")]
	[InlineData("copilot-api-token")]
	[InlineData("api-key")]
	public void Read_ForTokenAuthentication_DoesNotUseAuxiliaryLoginAsIdentity(string authType)
	{
		string json = ResponseJson
			.Replace("\"type\":\"token\"", $"\"type\":\"{authType}\"", StringComparison.Ordinal)
			.Replace("\"login\":\"synthetic-user\",\"copilotUser\"", "\"login\":\"different-user\",\"copilotUser\"", StringComparison.Ordinal);

		Assert.Equal(new CopilotSdkSubscriptionMetadata("individual", true), Read(json));
	}

	[Theory]
	[InlineData("false", "true", true)]
	[InlineData("true", "false", false)]
	[InlineData("true", "null", true)]
	[InlineData("false", "null", false)]
	[InlineData("null", "null", null)]
	public void Read_PreservesBillingTriStateAndQuotaPrecedence(string accountBilling, string quotaBilling, bool? expected)
	{
		string json = ResponseJson
			.Replace("\"token_based_billing\":false", $"\"token_based_billing\":{accountBilling}", StringComparison.Ordinal)
			.Replace("\"premium_interactions\":{\"token_based_billing\":true}", $"\"premium_interactions\":{{\"token_based_billing\":{quotaBilling}}}", StringComparison.Ordinal);

		Assert.Equal(expected, Read(json)?.IsTokenBasedBilling);
	}

	[Theory]
	[InlineData("")]
	[InlineData(",\"quota_snapshots\":null")]
	[InlineData(",\"quota_snapshots\":{}")]
	[InlineData(",\"quota_snapshots\":{\"premium_interactions\":null}")]
	[InlineData(",\"quota_snapshots\":{\"premium_interactions\":{}}")]
	public void Read_WhenBillingFieldsAreAbsent_DoesNotAssumeBillingMode(string quotaField)
	{
		string json = """
			{"authInfo":{"type":"token","host":"github.com","copilotUser":{"login":"synthetic-user"
			""" + quotaField + "}}}";

		Assert.Equal(new CopilotSdkSubscriptionMetadata(null, null), Read(json));
	}

	[Fact]
	public void Read_WithFreeAccessSku_OverridesGenericPlan()
	{
		string json = ResponseJson
			.Replace("\"individual\"", "\"github_copilot\"", StringComparison.Ordinal)
			.Replace("\"paid\"", "\" FREE_LIMITED_COPILOT \"", StringComparison.Ordinal);

		Assert.Equal(new CopilotSdkSubscriptionMetadata("free", true), Read(json));
	}

	[Theory]
	[InlineData("\"type\":\"token\"", "\"type\":123")]
	[InlineData("\"host\":\"github.com\"", "\"host\":false")]
	[InlineData("\"login\":\"synthetic-user\"", "\"login\":[]")]
	[InlineData("\"copilot_plan\":\"individual\"", "\"copilot_plan\":{}")]
	[InlineData("\"access_type_sku\":\"paid\"", "\"access_type_sku\":123")]
	[InlineData("\"token_based_billing\":false", "\"token_based_billing\":\"private-value\"")]
	[InlineData("\"token_based_billing\":true", "\"token_based_billing\":\"private-value\"")]
	[InlineData("\"authInfo\":{", "\"authInfo\":false,\"ignored\":{")]
	[InlineData("\"copilotUser\":{", "\"copilotUser\":[],\"ignored\":{")]
	[InlineData("\"quota_snapshots\":{", "\"quota_snapshots\":false,\"ignored\":{")]
	[InlineData("\"premium_interactions\":{", "\"premium_interactions\":[],\"ignored\":{")]
	public void Read_WithMalformedKnownField_RejectsWithoutDisclosingValues(string original, string replacement)
	{
		string json = ResponseJson.Replace(original, replacement, StringComparison.Ordinal);

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Read(json));

		Assert.DoesNotContain("private-value", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("synthetic-user", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("authInfo")]
	[InlineData("type")]
	[InlineData("host")]
	[InlineData("login")]
	[InlineData("copilotUser")]
	[InlineData("copilot_plan")]
	[InlineData("access_type_sku")]
	[InlineData("token_based_billing")]
	[InlineData("quota_snapshots")]
	[InlineData("premium_interactions")]
	public void Read_WithDuplicateCriticalField_RejectsAmbiguousMetadata(string fieldName)
	{
		string json = ResponseJson.Replace($"\"{fieldName}\":", $"\"{fieldName}\":null,\"{fieldName}\":", StringComparison.Ordinal);

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Read(json));

		Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("\"copilotUser\":{\"login\":", "\"copilotUser\":{\"login\":null,\"login\":")]
	[InlineData("\"premium_interactions\":{\"token_based_billing\":", "\"premium_interactions\":{\"token_based_billing\":null,\"token_based_billing\":")]
	public void Read_WithOnlyNestedCriticalDuplicate_RejectsAmbiguousMetadata(string original, string replacement)
	{
		string json = ResponseJson.Replace(original, replacement, StringComparison.Ordinal);

		InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Read(json));

		Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("null")]
	[InlineData("{\"authInfo\":{}}")]
	[InlineData("{\"authInfo\":{\"type\":\"unknown-private-type\"}}")]
	[InlineData("{\"authInfo\":{\"type\":\"token\",\"host\":\"github.com\"}}")]
	[InlineData("{\"authInfo\":{\"type\":\"token\",\"host\":\"github.com\",\"copilotUser\":{}}}")]
	[InlineData("{\"authInfo\":{\"type\":\"token\",\"host\":\"github.com\",\"copilotUser\":{\"login\":\" \"}}}")]
	public void Read_WithUnsupportedOrMissingRequiredMetadata_RejectsSafely(string json)
	{
		InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Read(json));

		Assert.DoesNotContain("unknown-private-type", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Read_WithUnknownAuxiliaryFields_IgnoresTheirShapeAndDuplicates()
	{
		string json = ResponseJson.Replace(
			"\"copilot_plan\":\"individual\"",
			"\"copilot_plan\":\"individual\",\"new_field\":{\"nested\":[true]},\"new_field\":123",
			StringComparison.Ordinal);

		Assert.Equal(new CopilotSdkSubscriptionMetadata("individual", true), Read(json));
	}

	private static CopilotSdkSubscriptionMetadata? Read(string json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		return CopilotSubscriptionMetadataReader.Read(document.RootElement, "github.com", "synthetic-user");
	}
}
