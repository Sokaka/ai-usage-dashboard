using System.IO;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal static class ClaudePlanTierReader
{
	internal const int MaximumFileSizeBytes = 1024 * 1024;
	private const string MaxPlanTier = "max";
	private const string UltraPlanTier = "ultra";
	private const string MaxOrganizationType = "claude_max";
	private const string Max20xRateLimitTier = "default_claude_max_20x";
	private const string OAuthAccountPropertyName = "oauthAccount";

	internal static string Read(
		string configDirectory,
		ClaudeSubscriptionContext context)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
		ArgumentNullException.ThrowIfNull(context);
		if (!CanRefinePlanTier(context))
		{
			return context.PlanTier;
		}

		string filePath = Path.GetFullPath(
			Path.Combine(configDirectory, ".claude.json"));
		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				"Claude plan metadata");
			using FileStream stream = new(
				filePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				filePath,
				"Claude plan metadata");
			if (stream.Length > MaximumFileSizeBytes)
			{
				throw new InvalidDataException("Claude plan metadata 超過大小上限。");
			}

			byte[] buffer = new byte[MaximumFileSizeBytes + 1];
			int byteCount = 0;
			while (byteCount < buffer.Length)
			{
				int bytesRead = stream.Read(buffer.AsSpan(byteCount));
				if (bytesRead == 0)
				{
					break;
				}

				byteCount += bytesRead;
			}

			if (byteCount > MaximumFileSizeBytes)
			{
				throw new InvalidDataException("Claude plan metadata 超過大小上限。");
			}

			using JsonDocument document = JsonDocument.Parse(
				buffer.AsMemory(0, byteCount));
			return ReadPlanTier(document.RootElement, context);
		}
		catch (FileNotFoundException)
		{
			return context.PlanTier;
		}
		catch (DirectoryNotFoundException)
		{
			return context.PlanTier;
		}
		catch (Exception exception) when (
			(exception is IOException) ||
			(exception is InvalidDataException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is JsonException))
		{
			throw new IOException(
				$"讀取 Claude plan metadata 失敗：{filePath}",
				exception);
		}
	}

	internal static string Parse(
		string json,
		ClaudeSubscriptionContext context)
	{
		ArgumentNullException.ThrowIfNull(json);
		ArgumentNullException.ThrowIfNull(context);
		if (!CanRefinePlanTier(context))
		{
			return context.PlanTier;
		}

		using JsonDocument document = JsonDocument.Parse(json);
		return ReadPlanTier(document.RootElement, context);
	}

	private static bool CanRefinePlanTier(ClaudeSubscriptionContext context)
	{
		return (context.VerificationState == SubscriptionVerificationState.Verified) &&
			string.Equals(context.PlanTier, MaxPlanTier, StringComparison.Ordinal);
	}

	private static string ReadPlanTier(
		JsonElement root,
		ClaudeSubscriptionContext context)
	{
		if ((root.ValueKind != JsonValueKind.Object) ||
			!TryGetUniqueOAuthAccount(root, out JsonElement oauthAccount) ||
			(oauthAccount.ValueKind != JsonValueKind.Object))
		{
			return context.PlanTier;
		}

		EnsureNoDuplicateMetadataProperties(oauthAccount);
		string? emailAddress = ReadOptionalText(oauthAccount, "emailAddress", 320);
		string? organizationUuid = ReadOptionalText(
			oauthAccount,
			"organizationUuid",
			256);
		string? organizationType = ReadOptionalText(
			oauthAccount,
			"organizationType",
			64);
		string? rateLimitTier = ReadOptionalText(
			oauthAccount,
			"organizationRateLimitTier",
			128);
		if (!string.Equals(
				emailAddress,
				context.AccountIdentity,
				StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(
				organizationUuid,
				context.SubscriptionScopeIdentity,
				StringComparison.Ordinal) ||
			!string.Equals(
				organizationType,
				MaxOrganizationType,
				StringComparison.Ordinal))
		{
			return context.PlanTier;
		}

		return string.Equals(
			rateLimitTier,
			Max20xRateLimitTier,
			StringComparison.Ordinal)
			? UltraPlanTier
			: context.PlanTier;
	}

	private static bool TryGetUniqueOAuthAccount(
		JsonElement root,
		out JsonElement oauthAccount)
	{
		oauthAccount = default;
		bool hasOAuthAccount = false;
		foreach (JsonProperty property in root.EnumerateObject())
		{
			if (!property.NameEquals(OAuthAccountPropertyName))
			{
				continue;
			}

			if (hasOAuthAccount)
			{
				throw new JsonException("Claude plan metadata 的 oauthAccount 重複。");
			}

			oauthAccount = property.Value;
			hasOAuthAccount = true;
		}

		return hasOAuthAccount;
	}

	private static void EnsureNoDuplicateMetadataProperties(JsonElement oauthAccount)
	{
		HashSet<string> seenProperties = new(StringComparer.Ordinal);
		foreach (JsonProperty property in oauthAccount.EnumerateObject())
		{
			if (property.Name is not (
				"emailAddress" or
				"organizationUuid" or
				"organizationType" or
				"organizationRateLimitTier"))
			{
				continue;
			}

			if (!seenProperties.Add(property.Name))
			{
				throw new JsonException(
					$"Claude plan metadata 的 {property.Name} 重複。");
			}
		}
	}

	private static string? ReadOptionalText(
		JsonElement oauthAccount,
		string propertyName,
		int maximumLength)
	{
		if (!oauthAccount.TryGetProperty(propertyName, out JsonElement value) ||
			(value.ValueKind != JsonValueKind.String))
		{
			return null;
		}

		string candidate = value.GetString()?.Trim() ?? string.Empty;
		return (candidate.Length == 0) ||
			(candidate.Length > maximumLength) ||
			candidate.Any(char.IsControl)
			? null
			: candidate;
	}
}
