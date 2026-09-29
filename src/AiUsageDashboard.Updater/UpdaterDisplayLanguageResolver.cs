using System.Security;
using System.Text.Json;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal static class UpdaterDisplayLanguageResolver
{
	internal const int MaximumPreferencesSizeBytes = 64 * 1024;
	private const int LanguageSchemaVersion = 8;
	private const int MaximumPreferencesDepth = 32;
	private const string PreferencesFileName = "preferences.json";

	internal static UpdaterDisplayLanguage Resolve(
		string userDataRoot,
		string? environmentLanguage,
		TextWriter diagnostics)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
		ArgumentNullException.ThrowIfNull(diagnostics);

		if (UpdaterDisplayLanguageContract.TryParse(environmentLanguage, out UpdaterDisplayLanguage language))
		{
			return language;
		}

		if (environmentLanguage is not null)
		{
			diagnostics.WriteLine(
				$"Ignoring unsupported {UpdaterDisplayLanguageContract.LanguageEnvironmentVariableName}; " +
				"reading the saved display language instead.");
		}

		string preferencesPath = Path.Combine(userDataRoot, PreferencesFileName);
		try
		{
			UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(userDataRoot);
			UpdateTransaction.ThrowIfNotOrdinaryFile(preferencesPath, mustExist: false);
			using FileStream stream = new(
				preferencesPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete);
			byte[] documentBytes = ReadBoundedPreferences(stream);
			using JsonDocument document = JsonDocument.Parse(
				documentBytes,
				new JsonDocumentOptions { MaxDepth = MaximumPreferencesDepth });
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidDataException("The preferences document must be an object.");
			}

			if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement schemaElement))
			{
				return UpdaterDisplayLanguage.English;
			}

			if ((schemaElement.ValueKind != JsonValueKind.Number) ||
				!schemaElement.TryGetInt32(out int schemaVersion) || (schemaVersion < 0))
			{
				throw new InvalidDataException("The preferences schemaVersion must be a non-negative integer.");
			}

			if (schemaVersion < LanguageSchemaVersion)
			{
				return UpdaterDisplayLanguage.English;
			}

			if (!document.RootElement.TryGetProperty("language", out JsonElement languageElement))
			{
				return UpdaterDisplayLanguage.English;
			}

			if ((languageElement.ValueKind == JsonValueKind.String) &&
				UpdaterDisplayLanguageContract.TryParse(languageElement.GetString(), out language))
			{
				return language;
			}

			throw new InvalidDataException("The saved display language is unsupported.");
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return UpdaterDisplayLanguage.English;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or SecurityException or
				InvalidDataException or JsonException or ArgumentException)
		{
			diagnostics.WriteLine(
				$"Could not read the updater display language from '{preferencesPath}'; " +
				$"using English: {exception.Message}");
			return UpdaterDisplayLanguage.English;
		}
	}

	private static byte[] ReadBoundedPreferences(FileStream stream)
	{
		if (stream.Length > MaximumPreferencesSizeBytes)
		{
			throw new InvalidDataException(
				$"The preferences document exceeds {MaximumPreferencesSizeBytes} bytes.");
		}

		byte[] buffer = new byte[MaximumPreferencesSizeBytes + 1];
		int byteCount = 0;
		while (byteCount < buffer.Length)
		{
			int bytesRead = stream.Read(buffer, byteCount, buffer.Length - byteCount);
			if (bytesRead == 0)
			{
				return buffer[..byteCount];
			}

			byteCount += bytesRead;
		}

		throw new InvalidDataException(
			$"The preferences document exceeds {MaximumPreferencesSizeBytes} bytes.");
	}
}
