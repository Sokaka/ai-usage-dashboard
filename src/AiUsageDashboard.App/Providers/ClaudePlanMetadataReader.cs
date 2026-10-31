using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Providers;

internal static class ClaudePlanMetadataReader
{
	private const string CredentialsFileName = ".credentials.json";
	private const int MaximumFileSizeBytes = 1024 * 1024;
	private const int MaximumJsonDepth = 16;

	internal static async Task<string?> ReadDisplayPlanAsync(
		string configDirectory,
		ClaudeSubscriptionContext context,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if ((context.VerificationState != SubscriptionVerificationState.Verified) ||
			!string.Equals(context.PlanTier, "max", StringComparison.Ordinal))
		{
			return null;
		}

		string credentialsPath = Path.Combine(configDirectory, CredentialsFileName);
		byte[]? buffer = null;
		try
		{
			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				credentialsPath,
				"Claude plan display metadata (.credentials.json)");
			await using FileStream stream = new(
				credentialsPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 1,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			if (stream.Length > MaximumFileSizeBytes)
			{
				throw new InvalidDataException(
					"Claude plan display metadata (.credentials.json) exceeds the size limit.");
			}

			buffer = new byte[(int)stream.Length + 1];
			int bytesRead = 0;
			while (bytesRead < buffer.Length)
			{
				int readCount = await stream.ReadAsync(
					buffer.AsMemory(bytesRead), cancellationToken);
				if (readCount == 0)
				{
					break;
				}

				bytesRead += readCount;
			}

			if (bytesRead == buffer.Length)
			{
				throw new InvalidDataException(
					"Claude plan display metadata (.credentials.json) grew during reading.");
			}

			cancellationToken.ThrowIfCancellationRequested();
			return ReadDisplayPlan(buffer.AsSpan(0, bytesRead));
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}
		catch (Exception exception) when (
			exception is JsonException or InvalidDataException or IOException or
				UnauthorizedAccessException)
		{
			// 例外訊息可能帶入 JSON 或私人路徑，診斷僅保留操作、檔名與錯誤型別。
			Trace.TraceWarning(
				"Claude plan display metadata read failed (.credentials.json; {0}).",
				exception is JsonException ? nameof(JsonException) : exception.GetType().Name);
			return null;
		}
		finally
		{
			if (buffer is not null)
			{
				CryptographicOperations.ZeroMemory(buffer);
			}
		}
	}

	private static string? ReadDisplayPlan(ReadOnlySpan<byte> json)
	{
		Utf8JsonReader reader = new(json, new JsonReaderOptions
		{
			MaxDepth = MaximumJsonDepth
		});
		if (!reader.Read() || (reader.TokenType != JsonTokenType.StartObject))
		{
			return null;
		}

		bool hasOauthMetadata = false;
		bool hasDuplicateOauthMetadata = false;
		string? displayPlan = null;
		while (reader.Read() && (reader.TokenType != JsonTokenType.EndObject))
		{
			bool isOauthMetadata = reader.ValueTextEquals("claudeAiOauth"u8);
			reader.Read();
			if (isOauthMetadata)
			{
				hasDuplicateOauthMetadata |= hasOauthMetadata;
				hasOauthMetadata = true;
				displayPlan = ReadOauthDisplayPlan(ref reader);
			}
			else
			{
				reader.Skip();
			}
		}

		reader.Read();
		return hasDuplicateOauthMetadata ? null : displayPlan;
	}

	private static string? ReadOauthDisplayPlan(ref Utf8JsonReader reader)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			reader.Skip();
			return null;
		}

		bool hasSubscriptionType = false;
		bool hasRateLimitTier = false;
		bool hasDuplicateField = false;
		bool isMax = false;
		string? displayPlan = null;
		while (reader.Read() && (reader.TokenType != JsonTokenType.EndObject))
		{
			bool isSubscriptionType = reader.ValueTextEquals("subscriptionType"u8);
			bool isRateLimitTier = reader.ValueTextEquals("rateLimitTier"u8);
			reader.Read();
			if (isSubscriptionType)
			{
				hasDuplicateField |= hasSubscriptionType;
				hasSubscriptionType = true;
				isMax = (reader.TokenType == JsonTokenType.String) &&
					reader.ValueTextEquals("max"u8);
			}
			else if (isRateLimitTier)
			{
				hasDuplicateField |= hasRateLimitTier;
				hasRateLimitTier = true;
				displayPlan = ReadRateLimitTier(ref reader);
			}

			// 未使用的欄位可能包含 token；不將它們轉成字串或保留在回傳值。
			reader.Skip();
		}

		return !hasDuplicateField && isMax ? displayPlan : null;
	}

	private static string? ReadRateLimitTier(ref Utf8JsonReader reader)
	{
		if (reader.TokenType != JsonTokenType.String)
		{
			return null;
		}

		if (reader.ValueTextEquals("default_claude_max_5x"u8))
		{
			return "max";
		}

		return reader.ValueTextEquals("default_claude_max_20x"u8) ? "ultra" : null;
	}
}
