using System.IO;
using System.Text.Json;

namespace AiUsageDashboard.App.Providers;

internal static class CopilotSubscriptionMetadataReader
{
	private const string FreeAccessTypeSku = "free_limited_copilot";

	internal static CopilotSdkSubscriptionMetadata? Read(
		JsonElement currentAuth,
		string? expectedHost,
		string? expectedLogin)
	{
		if (currentAuth.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException("Copilot subscription response must be an object.");
		}

		JsonElement auth = ReadOptionalObject(ReadProperty(currentAuth, "authInfo"), "authInfo");
		if (auth.ValueKind == JsonValueKind.Undefined)
		{
			return null;
		}

		string? authType = ReadOptionalString(ReadProperty(auth, "type"), "type");
		bool hasAuthLogin = authType switch
		{
			"env" or "user" or "gh-cli" => true,
			"hmac" or "token" or "copilot-api-token" or "api-key" => false,
			_ => throw new InvalidDataException("Copilot subscription authentication type is unsupported.")
		};
		string? host = ReadOptionalString(ReadProperty(auth, "host"), "host");
		JsonElement authLoginField = ReadProperty(auth, "login");
		if ((authType is "user" or "gh-cli") &&
			(authLoginField.ValueKind == JsonValueKind.Undefined))
		{
			throw new InvalidDataException("Copilot subscription authentication has no login field.");
		}
		string? authLogin = ReadOptionalString(authLoginField, "login");
		JsonElement user = ReadOptionalObject(ReadProperty(auth, "copilotUser"), "copilotUser");
		if (user.ValueKind == JsonValueKind.Undefined)
		{
			throw new InvalidDataException("Copilot subscription metadata has no account details.");
		}

		string? userLogin = ReadOptionalString(ReadProperty(user, "login"), "login");
		if (string.IsNullOrWhiteSpace(userLogin))
		{
			throw new InvalidDataException("Copilot subscription metadata has no account login.");
		}

		EnsureMatchingIdentity(host, userLogin, hasAuthLogin ? authLogin : null, expectedHost, expectedLogin);
		return ReadSubscription(user);
	}

	private static CopilotSdkSubscriptionMetadata ReadSubscription(JsonElement user)
	{
		string? planTier = ReadOptionalString(ReadProperty(user, "copilot_plan"), "copilot_plan");
		string? accessTypeSku = ReadOptionalString(ReadProperty(user, "access_type_sku"), "access_type_sku");
		bool? accountBilling = ReadOptionalBoolean(ReadProperty(user, "token_based_billing"), "token_based_billing");
		JsonElement quotas = ReadOptionalObject(ReadProperty(user, "quota_snapshots"), "quota_snapshots");
		JsonElement premium = ReadOptionalObject(ReadProperty(quotas, "premium_interactions"), "premium_interactions");
		bool? quotaBilling = ReadOptionalBoolean(ReadProperty(premium, "token_based_billing"), "token_based_billing");
		if (string.Equals(accessTypeSku?.Trim(), FreeAccessTypeSku, StringComparison.OrdinalIgnoreCase))
		{
			planTier = "free";
		}

		return new CopilotSdkSubscriptionMetadata(planTier, quotaBilling ?? accountBilling);
	}

	private static void EnsureMatchingIdentity(
		string? host,
		string userLogin,
		string? authLogin,
		string? expectedHost,
		string? expectedLogin)
	{
		if (!CopilotAccountIdentityRules.TryNormalizeHost(expectedHost, out string normalizedExpectedHost) ||
			!CopilotAccountIdentityRules.TryNormalizeHost(host, out string normalizedObservedHost) ||
			!string.Equals(normalizedExpectedHost, normalizedObservedHost, StringComparison.Ordinal) ||
			string.IsNullOrWhiteSpace(expectedLogin) ||
			!string.Equals(userLogin.Trim(), expectedLogin.Trim(), StringComparison.OrdinalIgnoreCase) ||
			(!string.IsNullOrWhiteSpace(authLogin) &&
				!string.Equals(authLogin.Trim(), expectedLogin.Trim(), StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("Copilot subscription metadata identity does not match the authenticated account.");
		}
	}

	private static JsonElement ReadProperty(JsonElement owner, string propertyName)
	{
		if (owner.ValueKind == JsonValueKind.Undefined)
		{
			return default;
		}

		JsonElement result = default;
		bool found = false;
		foreach (JsonProperty property in owner.EnumerateObject())
		{
			if (!property.NameEquals(propertyName))
			{
				continue;
			}

			if (found)
			{
				throw new InvalidDataException($"Copilot subscription metadata contains a duplicate '{propertyName}' field.");
			}

			found = true;
			result = property.Value;
		}

		return result;
	}

	private static JsonElement ReadOptionalObject(JsonElement value, string propertyName)
	{
		return value.ValueKind switch
		{
			JsonValueKind.Undefined or JsonValueKind.Null => default,
			JsonValueKind.Object => value,
			_ => throw CreateInvalidFieldException(propertyName)
		};
	}

	private static string? ReadOptionalString(JsonElement value, string propertyName)
	{
		return value.ValueKind switch
		{
			JsonValueKind.Undefined or JsonValueKind.Null => null,
			JsonValueKind.String => value.GetString(),
			_ => throw CreateInvalidFieldException(propertyName)
		};
	}

	private static bool? ReadOptionalBoolean(JsonElement value, string propertyName)
	{
		return value.ValueKind switch
		{
			JsonValueKind.Undefined or JsonValueKind.Null => null,
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => throw CreateInvalidFieldException(propertyName)
		};
	}

	private static InvalidDataException CreateInvalidFieldException(string propertyName)
	{
		return new InvalidDataException($"Copilot subscription metadata field '{propertyName}' has an invalid type.");
	}
}
