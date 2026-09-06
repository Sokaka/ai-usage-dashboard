using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsageDashboard.AntigravitySpike;

public enum AntigravityStatusLineIntegrationStatus
{
	Installed,
	AlreadyInstalled,
	ExistingCustomStatusLine,
	SettingsUnavailable,
	HelperUnavailable,
	PrivateStorageUnavailable,
	PathUnsupported,
	Failed
}

public readonly record struct AntigravityStatusLineIntegrationResult(
	AntigravityStatusLineIntegrationStatus Status)
{
	public bool IsOwnedConfiguration =>
		Status is AntigravityStatusLineIntegrationStatus.Installed or
			AntigravityStatusLineIntegrationStatus.AlreadyInstalled;
}

public static partial class AntigravityStatusLineIntegration
{
	public const string PackagedHelperFileName =
		"AiUsageDashboard.AntigravityCapture.exe";
	public const string OwnershipMarker =
		"--ai-usage-dashboard-agy-statusline-v1";
	private const int MaximumSettingsBytes = 1024 * 1024;
	private const int MaximumRollbackFingerprintBytes = 16 * 1024 * 1024;
	private const int MaximumHelperBytes = 128 * 1024 * 1024;
	private const int MaximumNativePathCharacters = 32 * 1024;
	private const int MaximumVerificationCacheEntries = 8;
	private const int MaximumRetainedHelperGenerations = 2;
	private static readonly TimeSpan MutationMutexWaitTimeout =
		TimeSpan.FromSeconds(3);
	private static readonly TimeSpan HelperHashRevalidationInterval =
		TimeSpan.FromMinutes(5);
	private const AccessControlSections SettingsSecuritySections =
		AccessControlSections.Access |
		AccessControlSections.Owner |
		AccessControlSections.Group;
	private const FileSystemRights CommittedSettingsRepairRights =
		FileSystemRights.ReadAttributes |
		FileSystemRights.ReadPermissions |
		FileSystemRights.ChangePermissions |
		FileSystemRights.Synchronize;
	private const FileSystemRights SettingsStateReadRights =
		FileSystemRights.ReadData |
		FileSystemRights.ReadAttributes |
		FileSystemRights.ReadPermissions |
		FileSystemRights.Synchronize;
	private static readonly UTF8Encoding StrictUtf8 = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);
	private static readonly object HelperPreparationLock = new();
	private static readonly object IntegrationMutationLock = new();
	private static readonly Dictionary<string, HelperVerificationCacheEntry>
		HelperVerificationCache = new(StringComparer.OrdinalIgnoreCase);
	private static long _helperVerificationSequence;

	private readonly record struct HelperFileStamp(
		long Length,
		DateTime CreationTimeUtc,
		DateTime LastWriteTimeUtc);

	private sealed record HelperVerificationCacheEntry(
		HelperFileStamp Stamp,
		byte[] Hash,
		DateTimeOffset VerifiedAtUtc,
		long Sequence);

	private sealed class CrossProcessMutationLease : IDisposable
	{
		private Mutex? _mutex;

		internal CrossProcessMutationLease(Mutex mutex)
		{
			_mutex = mutex;
		}

		public void Dispose()
		{
			Mutex? mutex = Interlocked.Exchange(ref _mutex, null);

			if (mutex is null)
			{
				return;
			}

			try
			{
				mutex.ReleaseMutex();
			}
			catch (ApplicationException)
			{
				// A lease is always released on its acquiring thread. Fail closed
				// if the runtime reports that ownership was already lost.
			}
			finally
			{
				mutex.Dispose();
			}
		}
	}

	private enum StatusLineConfigurationKind
	{
		Missing,
		OwnedEnabled,
		OwnedDisabled,
		Custom
	}

	public static AntigravityStatusLineIntegrationResult TryEnsureInstalled(
		string packagedHelperPath)
	{
		try
		{
			string? settingsPath = TryGetDefaultSettingsPath();
			string? capturePath = AntigravityStatusLineCapture.GetCaptureFilePath();
			string? privateRoot = capturePath is null
				? null
				: Path.GetDirectoryName(capturePath);

			if (settingsPath is null || privateRoot is null)
			{
				return new(
					AntigravityStatusLineIntegrationStatus.SettingsUnavailable);
			}

			return TryEnsureInstalled(
				packagedHelperPath,
				settingsPath,
				privateRoot);
		}
		catch
		{
			return new(AntigravityStatusLineIntegrationStatus.Failed);
		}
	}

	public static bool IsOwnedStatusLineConfigured()
	{
		try
		{
			string? settingsPath = TryGetDefaultSettingsPath();
			string? capturePath = AntigravityStatusLineCapture.GetCaptureFilePath();
			string? privateRoot = capturePath is null
				? null
				: Path.GetDirectoryName(capturePath);

			return settingsPath is not null &&
				privateRoot is not null &&
				IsOwnedStatusLineConfigured(settingsPath, privateRoot);
		}
		catch
		{
			return false;
		}
	}

	public static bool TryRemoveOwnedStatusLine()
	{
		try
		{
			string? settingsPath = TryGetDefaultSettingsPath();
			string? capturePath = AntigravityStatusLineCapture.GetCaptureFilePath();
			string? privateRoot = capturePath is null
				? null
				: Path.GetDirectoryName(capturePath);

			return settingsPath is not null &&
				capturePath is not null &&
				privateRoot is not null &&
				TryRemoveOwnedStatusLine(
					settingsPath,
					privateRoot,
					capturePath);
		}
		catch
		{
			return false;
		}
	}

	private static CrossProcessMutationLease?
		TryAcquireCrossProcessMutationLease(string settingsPath)
	{
		Mutex? mutex = null;
		bool acquired = false;

		try
		{
			string normalizedSettingsPath = Path
				.GetFullPath(settingsPath)
				.ToUpperInvariant();
			byte[] pathBytes = Encoding.UTF8.GetBytes(normalizedSettingsPath);
			string pathHash;

			try
			{
				pathHash = Convert.ToHexString(SHA256.HashData(pathBytes));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(pathBytes);
			}

			mutex = new Mutex(
				initiallyOwned: false,
				name:
					$"Local\\AiUsageDashboard.AntigravityStatusLine.{pathHash}");

			try
			{
				acquired = mutex.WaitOne(MutationMutexWaitTimeout);
			}
			catch (AbandonedMutexException)
			{
				// An abandoned mutex is acquired by the waiting thread. All state
				// is re-read below, so it is safe to continue from this boundary.
				acquired = true;
			}

			if (!acquired)
			{
				return null;
			}

			CrossProcessMutationLease lease = new(mutex);
			mutex = null;
			return lease;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (mutex is not null)
			{
				if (acquired)
				{
					try
					{
						mutex.ReleaseMutex();
					}
					catch (ApplicationException)
					{
					}
				}

				mutex.Dispose();
			}
		}
	}

	internal static AntigravityStatusLineIntegrationResult TryEnsureInstalled(
		string packagedHelperPath,
		string settingsPath,
		string privateRoot)
	{
		try
		{
			lock (IntegrationMutationLock)
			{
			using CrossProcessMutationLease? mutationLease =
				TryAcquireCrossProcessMutationLease(settingsPath);

			if (mutationLease is null)
			{
				return new(AntigravityStatusLineIntegrationStatus.Failed);
			}

			if (!TryReadSettings(settingsPath, out byte[]? originalBytes) ||
				originalBytes is null)
			{
				return new(
					AntigravityStatusLineIntegrationStatus.SettingsUnavailable);
			}

			try
			{
				if (!TryInspectStatusLineConfiguration(
						originalBytes,
						privateRoot,
						out StatusLineConfigurationKind configurationKind,
						out string? previousOwnedHelperPath))
				{
					return new(AntigravityStatusLineIntegrationStatus.Failed);
				}

				if (configurationKind == StatusLineConfigurationKind.Custom)
				{
					return new(
						AntigravityStatusLineIntegrationStatus
							.ExistingCustomStatusLine);
				}

				// A disabled owned status line cannot produce an account display.
				// Avoid copying or upgrading an executable that AGY will not run.
				if (configurationKind == StatusLineConfigurationKind.OwnedDisabled)
				{
					if (!string.IsNullOrEmpty(previousOwnedHelperPath))
					{
						TryPruneHelperGenerations(
							settingsPath,
							privateRoot,
							previousOwnedHelperPath,
							previousOwnedHelperPath);
					}

					return new(
						AntigravityStatusLineIntegrationStatus.AlreadyInstalled);
				}

				// The deployed helper stays in the ACL-protected capture directory.
				// Command construction below selects a shell-safe representation
				// for AGY's cmd.exe runner without weakening path validation.
				if (!TryPrepareHelper(
						packagedHelperPath,
						privateRoot,
						out string? privateHelperPath) ||
					privateHelperPath is null)
				{
					return new(
						AntigravityStatusLineIntegrationStatus.HelperUnavailable);
				}

				if (!TryBuildOwnedCommand(
						privateHelperPath,
						out string? command) ||
					command is null)
				{
					return new(
						AntigravityStatusLineIntegrationStatus.PathUnsupported);
				}

				if (!TryCreateUpdatedSettings(
						originalBytes,
						command,
						privateRoot,
						out byte[]? updatedBytes,
						out bool wasAlreadyInstalled,
						out bool hasCustomStatusLine))
				{
					return new(hasCustomStatusLine
						? AntigravityStatusLineIntegrationStatus
							.ExistingCustomStatusLine
						: AntigravityStatusLineIntegrationStatus.Failed);
				}

				if (wasAlreadyInstalled)
				{
					TryPruneHelperGenerations(
						settingsPath,
						privateRoot,
						privateHelperPath,
						previousOwnedHelperPath);
					return new(
						AntigravityStatusLineIntegrationStatus.AlreadyInstalled);
				}

				if (updatedBytes is null ||
					!TryReplaceSettingsAtomically(
						settingsPath,
						originalBytes,
						updatedBytes))
				{
					return new(AntigravityStatusLineIntegrationStatus.Failed);
				}

				TryPruneHelperGenerations(
					settingsPath,
					privateRoot,
					privateHelperPath,
					previousOwnedHelperPath);

				return new(AntigravityStatusLineIntegrationStatus.Installed);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(originalBytes);
			}
			}
		}
		catch
		{
			return new(AntigravityStatusLineIntegrationStatus.Failed);
		}
	}

	internal static bool IsOwnedStatusLineConfigured(
		string settingsPath,
		string privateRoot)
	{
		if (!TryReadSettings(settingsPath, out byte[]? settingsBytes) ||
			settingsBytes is null)
		{
			return false;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(
				settingsBytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 32
				});

			if (document.RootElement.ValueKind != JsonValueKind.Object ||
				!HasUniqueProperties(document.RootElement) ||
				!document.RootElement.TryGetProperty(
					"statusLine",
					out JsonElement statusLine) ||
				statusLine.ValueKind != JsonValueKind.Object ||
				!statusLine.TryGetProperty("type", out JsonElement type) ||
				type.ValueKind != JsonValueKind.String ||
				!string.Equals(
					type.GetString(),
					"command",
					StringComparison.Ordinal) ||
				!statusLine.TryGetProperty("command", out JsonElement command) ||
				command.ValueKind != JsonValueKind.String ||
				!TryGetOwnedHelperPath(
					command.GetString(),
					privateRoot,
					out string? helperPath) ||
				helperPath is null ||
				!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
					helperPath))
			{
				return false;
			}

			return TryGetStatusLineEnabled(statusLine, out bool isEnabled) &&
				isEnabled;
		}
		catch
		{
			return false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(settingsBytes);
		}
	}

	internal static bool TryRemoveOwnedStatusLine(
		string settingsPath,
		string privateRoot,
		string captureFilePath,
		Action? beforeArtifactCleanup = null)
	{
		lock (IntegrationMutationLock)
		{
		using CrossProcessMutationLease? mutationLease =
			TryAcquireCrossProcessMutationLease(settingsPath);

		if (mutationLease is null)
		{
			return false;
		}

		if (!TryReadSettings(settingsPath, out byte[]? originalBytes) ||
			originalBytes is null)
		{
			return false;
		}

		try
		{
			if (!TryInspectStatusLineConfiguration(
					originalBytes,
					privateRoot,
					out StatusLineConfigurationKind configurationKind,
					out string? configuredOwnedHelperPath))
			{
				return false;
			}

			if (configurationKind is
				StatusLineConfigurationKind.OwnedEnabled or
				StatusLineConfigurationKind.OwnedDisabled)
			{
				if (!TryPrepareConfiguredHelperForRemoval(
						configuredOwnedHelperPath,
						privateRoot))
				{
					return false;
				}

				if (!TryCreateSettingsWithoutOwnedStatusLine(
						originalBytes,
						privateRoot,
						out byte[]? updatedBytes) ||
					updatedBytes is null ||
					!TryReplaceSettingsAtomically(
						settingsPath,
						originalBytes,
						updatedBytes))
				{
					return false;
				}
			}
			else if (configurationKind != StatusLineConfigurationKind.Missing)
			{
				return false;
			}

			beforeArtifactCleanup?.Invoke();
			return TryDeleteOwnedArtifacts(
				settingsPath,
				privateRoot,
				captureFilePath,
				configuredOwnedHelperPath);
		}
		catch
		{
			return false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(originalBytes);
		}
		}
	}

	private static bool TryPrepareConfiguredHelperForRemoval(
		string? configuredOwnedHelperPath,
		string privateRoot)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(configuredOwnedHelperPath))
			{
				return false;
			}

			string fullRoot = Path.GetFullPath(privateRoot);
			string fullHelperPath = Path.GetFullPath(configuredOwnedHelperPath);

			if (!string.Equals(
					Path.GetDirectoryName(fullHelperPath),
					fullRoot,
					StringComparison.OrdinalIgnoreCase) ||
				!OwnedHelperFileNameRegex().IsMatch(
					Path.GetFileName(fullHelperPath)))
			{
				return false;
			}

			if (IsPathAbsent(fullHelperPath))
			{
				return true;
			}

			if (!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
					fullHelperPath))
			{
				return false;
			}

			// A helper copied by an older build may safely inherit the already
			// private parent ACL. Harden this exact configured path before removing
			// the setting so cleanup-only retries can still recognize it as owned;
			// unrelated safely inherited lookalikes remain untouched.
			return AntigravityPrivateKeyAcl.IsPrivateFile(fullHelperPath) ||
				AntigravityPrivateKeyAcl.TryProtectNewFile(fullHelperPath);
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryCreateSettingsWithoutOwnedStatusLine(
		ReadOnlyMemory<byte> originalBytes,
		string privateRoot,
		out byte[]? updatedBytes)
	{
		updatedBytes = null;

		try
		{
			if (!TryInspectStatusLineConfiguration(
					originalBytes,
					privateRoot,
					out StatusLineConfigurationKind configurationKind,
					out _) ||
				configurationKind is not (
					StatusLineConfigurationKind.OwnedEnabled or
					StatusLineConfigurationKind.OwnedDisabled))
			{
				return false;
			}

			using JsonDocument document = JsonDocument.Parse(
				originalBytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 32
				});
			using MemoryStream output = new();

			using (Utf8JsonWriter writer = new(
				output,
				new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();

				foreach (JsonProperty property in
					document.RootElement.EnumerateObject())
				{
					if (!property.NameEquals("statusLine"))
					{
						property.WriteTo(writer);
					}
				}

				writer.WriteEndObject();
			}

			updatedBytes = output.ToArray();
			return updatedBytes.Length <= MaximumSettingsBytes;
		}
		catch
		{
			updatedBytes = null;
			return false;
		}
	}

	internal static bool TryCreateUpdatedSettings(
		ReadOnlyMemory<byte> originalBytes,
		string command,
		string privateRoot,
		out byte[]? updatedBytes,
		out bool wasAlreadyInstalled,
		out bool hasCustomStatusLine)
	{
		updatedBytes = null;
		wasAlreadyInstalled = false;
		hasCustomStatusLine = false;

		try
		{
			_ = StrictUtf8.GetString(originalBytes.Span);
			using JsonDocument document = JsonDocument.Parse(
				originalBytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 32
				});

			if (document.RootElement.ValueKind != JsonValueKind.Object ||
				!HasUniqueProperties(document.RootElement))
			{
				return false;
			}

			bool hasStatusLine = document.RootElement.TryGetProperty(
				"statusLine",
				out JsonElement existingStatusLine);
			bool isOwned = hasStatusLine &&
				existingStatusLine.ValueKind == JsonValueKind.Object &&
				existingStatusLine.TryGetProperty(
					"type",
					out JsonElement existingType) &&
				existingType.ValueKind == JsonValueKind.String &&
				string.Equals(
					existingType.GetString(),
					"command",
					StringComparison.Ordinal) &&
				existingStatusLine.TryGetProperty(
					"command",
					out JsonElement existingCommand) &&
				existingCommand.ValueKind == JsonValueKind.String &&
				TryGetOwnedHelperPath(
					existingCommand.GetString(),
					privateRoot,
					out _);

			if (isOwned &&
				!TryGetStatusLineEnabled(existingStatusLine, out _))
			{
				return false;
			}

			if (hasStatusLine && !isOwned)
			{
				hasCustomStatusLine = true;
				return false;
			}

			if (isOwned && string.Equals(
				existingStatusLine.GetProperty("command").GetString(),
				command,
				StringComparison.Ordinal))
			{
				wasAlreadyInstalled = true;
				return true;
			}

			using MemoryStream output = new();
			using (Utf8JsonWriter writer = new(
				output,
				new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();

				foreach (JsonProperty property in
					document.RootElement.EnumerateObject())
				{
					if (property.NameEquals("statusLine"))
					{
						WriteOwnedStatusLine(writer, existingStatusLine, command);
					}
					else
					{
						property.WriteTo(writer);
					}
				}

				if (!hasStatusLine)
				{
					WriteOwnedStatusLine(writer, null, command);
				}

				writer.WriteEndObject();
			}

			updatedBytes = output.ToArray();
			return updatedBytes.Length <= MaximumSettingsBytes;
		}
		catch
		{
			updatedBytes = null;
			return false;
		}
	}

	private static void WriteOwnedStatusLine(
		Utf8JsonWriter writer,
		JsonElement? existingStatusLine,
		string command)
	{
		writer.WritePropertyName("statusLine");
		writer.WriteStartObject();

		if (existingStatusLine is JsonElement existing)
		{
			foreach (JsonProperty property in existing.EnumerateObject())
			{
				if (property.NameEquals("command"))
				{
					writer.WriteString("command", command);
				}
				else
				{
					property.WriteTo(writer);
				}
			}
		}
		else
		{
			writer.WriteString("type", "command");
			writer.WriteString("command", command);
			writer.WriteBoolean("enabled", true);
			writer.WriteBoolean("stack_with_default", true);
		}

		writer.WriteEndObject();
	}

	private static bool HasUniqueProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> names = new(StringComparer.Ordinal);

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (!names.Add(property.Name) ||
					!HasUniqueProperties(property.Value))
				{
					return false;
				}
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement child in element.EnumerateArray())
			{
				if (!HasUniqueProperties(child))
				{
					return false;
				}
			}
		}

		return true;
	}

	private static bool TryInspectStatusLineConfiguration(
		ReadOnlyMemory<byte> settingsBytes,
		string privateRoot,
		out StatusLineConfigurationKind configurationKind,
		out string? ownedHelperPath)
	{
		configurationKind = StatusLineConfigurationKind.Custom;
		ownedHelperPath = null;

		try
		{
			_ = StrictUtf8.GetString(settingsBytes.Span);
			using JsonDocument document = JsonDocument.Parse(
				settingsBytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 32
				});

			if (document.RootElement.ValueKind != JsonValueKind.Object ||
				!HasUniqueProperties(document.RootElement))
			{
				return false;
			}

			if (!document.RootElement.TryGetProperty(
					"statusLine",
					out JsonElement statusLine))
			{
				configurationKind = StatusLineConfigurationKind.Missing;
				return true;
			}

			if (statusLine.ValueKind != JsonValueKind.Object ||
				!statusLine.TryGetProperty("type", out JsonElement type) ||
				type.ValueKind != JsonValueKind.String ||
				!string.Equals(
					type.GetString(),
					"command",
					StringComparison.Ordinal) ||
				!statusLine.TryGetProperty(
					"command",
					out JsonElement command) ||
				command.ValueKind != JsonValueKind.String ||
				!TryGetOwnedHelperPath(
					command.GetString(),
					privateRoot,
					out ownedHelperPath))
			{
				configurationKind = StatusLineConfigurationKind.Custom;
				return true;
			}

			if (!TryGetStatusLineEnabled(statusLine, out bool isEnabled))
			{
				return false;
			}

			configurationKind = isEnabled
				? StatusLineConfigurationKind.OwnedEnabled
				: StatusLineConfigurationKind.OwnedDisabled;
			return true;
		}
		catch
		{
			configurationKind = StatusLineConfigurationKind.Custom;
			ownedHelperPath = null;
			return false;
		}
	}

	private static bool TryGetStatusLineEnabled(
		JsonElement statusLine,
		out bool isEnabled)
	{
		isEnabled = true;

		if (!statusLine.TryGetProperty("enabled", out JsonElement enabled))
		{
			return true;
		}

		if (enabled.ValueKind == JsonValueKind.True)
		{
			return true;
		}

		if (enabled.ValueKind == JsonValueKind.False)
		{
			isEnabled = false;
			return true;
		}

		return false;
	}

	internal static string BuildOwnedCommand(string helperPath)
	{
		ArgumentException.ThrowIfNullOrEmpty(helperPath);

		if (IsSafeUnquotedCommandPath(helperPath))
		{
			return $"{helperPath} {OwnershipMarker}";
		}

		if (IsSafeQuotedCommandPath(helperPath))
		{
			// AGY 1.1.12 cannot execute a command whose first token is a
			// quoted path. Prefixing the fixed CALL builtin keeps the quoted
			// path away from that runner edge while preserving argv exactly.
			return $"call \"{helperPath}\" {OwnershipMarker}";
		}

		throw new ArgumentException(
			"The helper path cannot be represented safely for cmd.exe.",
			nameof(helperPath));
	}

	private static bool TryBuildOwnedCommand(
		string helperPath,
		out string? command)
	{
		command = null;

		try
		{
			command = BuildOwnedCommand(helperPath);
			return true;
		}
		catch (ArgumentException)
		{
			// A verified 8.3 alias contains only shell-safe characters while
			// still resolving to the protected, content-addressed helper.
			if (!TryGetVerifiedShortPath(helperPath, out string? shortPath) ||
				shortPath is null)
			{
				return false;
			}

			try
			{
				command = BuildOwnedCommand(shortPath);
				return true;
			}
			catch (ArgumentException)
			{
				command = null;
				return false;
			}
		}
	}

	private static bool TryPrepareHelper(
		string packagedHelperPath,
		string privateRoot,
		out string? privateHelperPath)
	{
		privateHelperPath = null;

		try
		{
			if (!Path.IsPathFullyQualified(packagedHelperPath) ||
				!Path.IsPathFullyQualified(privateRoot) ||
				!File.Exists(packagedHelperPath) ||
				!string.Equals(
					Path.GetFileName(packagedHelperPath),
					PackagedHelperFileName,
					StringComparison.OrdinalIgnoreCase) ||
				!AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
					privateRoot,
					out _,
					out _))
			{
				return false;
			}

			lock (HelperPreparationLock)
			{
				if (!TryGetVerifiedHelperHash(
						packagedHelperPath,
						out byte[]? sourceHash) ||
					sourceHash is null)
				{
					return false;
				}

				string hashPrefix = Convert.ToHexString(sourceHash)
					.ToLowerInvariant()[..16];
				privateHelperPath = Path.Combine(
					privateRoot,
					$"AiUsageDashboard.AntigravityCapture-{hashPrefix}.exe");

				if (File.Exists(privateHelperPath))
				{
					return AntigravityPrivateKeyAcl
						.IsPrivateOrSafelyInheritedFile(privateHelperPath) &&
						TryGetVerifiedHelperHash(
							privateHelperPath,
							out byte[]? privateHash) &&
						privateHash is not null &&
						CryptographicOperations.FixedTimeEquals(
							privateHash,
							sourceHash);
				}

				string temporaryPath = Path.Combine(
					privateRoot,
					$".helper-{Guid.NewGuid():N}.tmp");

				try
				{
					using (FileStream input = new(
						packagedHelperPath,
						FileMode.Open,
						FileAccess.Read,
						FileShare.Read,
						bufferSize: 1,
						FileOptions.SequentialScan))
					using (FileStream output = new(
						temporaryPath,
						FileMode.CreateNew,
						FileAccess.Write,
						FileShare.None,
						81920,
						FileOptions.WriteThrough))
					{
						input.CopyTo(output);
						output.Flush(flushToDisk: true);
					}

					if (!AntigravityPrivateKeyAcl.TryProtectNewFile(temporaryPath) ||
						!TryGetVerifiedHelperHash(
							temporaryPath,
							out byte[]? temporaryHash) ||
						temporaryHash is null ||
						!CryptographicOperations.FixedTimeEquals(
							temporaryHash,
							sourceHash))
					{
						return false;
					}

					File.Move(temporaryPath, privateHelperPath, overwrite: false);
					RemoveHelperVerificationCacheEntry(temporaryPath);
					return AntigravityPrivateKeyAcl.IsPrivateFile(privateHelperPath) &&
						TryGetVerifiedHelperHash(
							privateHelperPath,
							out byte[]? committedHash) &&
						committedHash is not null &&
						CryptographicOperations.FixedTimeEquals(
							committedHash,
							sourceHash);
				}
				finally
				{
					RemoveHelperVerificationCacheEntry(temporaryPath);
					TryDeleteFile(temporaryPath);
				}
			}
		}
		catch
		{
			privateHelperPath = null;
			return false;
		}
	}

	private static bool TryGetVerifiedHelperHash(
		string path,
		out byte[]? hash)
	{
		hash = null;

		try
		{
			string fullPath = Path.GetFullPath(path);

			if (!Path.IsPathFullyQualified(path) ||
				!File.Exists(fullPath) ||
				(File.GetAttributes(fullPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
				!TryGetHelperFileStamp(fullPath, out HelperFileStamp stamp))
			{
				RemoveHelperVerificationCacheEntry(fullPath);
				return false;
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;

		if (HelperVerificationCache.TryGetValue(
					fullPath,
					out HelperVerificationCacheEntry? cached))
			{
				TimeSpan age = now - cached.VerifiedAtUtc;

				if (cached.Stamp == stamp &&
					age >= TimeSpan.Zero &&
					age < HelperHashRevalidationInterval)
				{
					HelperVerificationCache[fullPath] = cached with
					{
						Sequence = ++_helperVerificationSequence
					};
					hash = cached.Hash;
					return true;
				}
			}

			byte[] computedHash;

			using (FileStream stream = new(
				fullPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 1,
				FileOptions.SequentialScan))
			{
				if ((stream.Length <= 0) ||
					(stream.Length > MaximumHelperBytes))
				{
					RemoveHelperVerificationCacheEntry(fullPath);
					return false;
				}

				computedHash = SHA256.HashData(stream);
			}

			if ((File.GetAttributes(fullPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
				!TryGetHelperFileStamp(
					fullPath,
					out HelperFileStamp verifiedStamp) ||
				verifiedStamp != stamp)
			{
				CryptographicOperations.ZeroMemory(computedHash);
				RemoveHelperVerificationCacheEntry(fullPath);
				return false;
			}

			AddHelperVerificationCacheEntry(
				fullPath,
				verifiedStamp,
				computedHash,
				now);
			hash = computedHash;
			return true;
		}
		catch
		{
			RemoveHelperVerificationCacheEntry(path);
			return false;
		}
	}

	private static bool TryGetHelperFileStamp(
		string path,
		out HelperFileStamp stamp)
	{
		FileInfo file = new(path);
		file.Refresh();

		if (!file.Exists ||
			(file.Length <= 0) ||
			(file.Length > MaximumHelperBytes))
		{
			stamp = default;
			return false;
		}

		stamp = new(
			file.Length,
			file.CreationTimeUtc,
			file.LastWriteTimeUtc);
		return true;
	}

	private static void AddHelperVerificationCacheEntry(
		string fullPath,
		HelperFileStamp stamp,
		byte[] hash,
		DateTimeOffset verifiedAtUtc)
	{
		_ = HelperVerificationCache.Remove(fullPath);

		HelperVerificationCache.Add(
			fullPath,
			new(
				stamp,
				hash,
				verifiedAtUtc,
				++_helperVerificationSequence));

		while (HelperVerificationCache.Count > MaximumVerificationCacheEntries)
		{
			KeyValuePair<string, HelperVerificationCacheEntry> oldest =
				HelperVerificationCache.MinBy(pair => pair.Value.Sequence);
			HelperVerificationCache.Remove(oldest.Key);
		}
	}

	private static void RemoveHelperVerificationCacheEntry(string path)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);

			_ = HelperVerificationCache.Remove(fullPath);
		}
		catch
		{
		}
	}

	internal static void TryPruneHelperGenerations(
		string settingsPath,
		string privateRoot,
		string currentHelperPath,
		string? previousOwnedHelperPath)
	{
		try
		{
			lock (HelperPreparationLock)
			{
				string fullRoot = Path.GetFullPath(privateRoot);
				string fullCurrent = Path.GetFullPath(currentHelperPath);

				if (!TryGetCurrentlyConfiguredOwnedHelperPath(
						settingsPath,
						fullRoot,
						out string? configuredHelperPath) ||
					configuredHelperPath is null)
				{
					// Settings may have changed outside our mutex protocol. Never
					// retire a helper unless the current owner can be re-established.
					return;
				}

				HashSet<string> retained = new(StringComparer.OrdinalIgnoreCase)
				{
					fullCurrent,
					configuredHelperPath
				};

				if (!string.IsNullOrEmpty(previousOwnedHelperPath))
				{
					string fullPrevious = Path.GetFullPath(previousOwnedHelperPath);

					if (!string.Equals(
							fullPrevious,
							fullCurrent,
							StringComparison.OrdinalIgnoreCase) &&
						File.Exists(fullPrevious))
					{
						retained.Add(fullPrevious);
					}
				}

				List<FileInfo> candidates = Directory
					.EnumerateFiles(
						fullRoot,
						"AiUsageDashboard.AntigravityCapture-*.exe",
						SearchOption.TopDirectoryOnly)
					.Select(path => new FileInfo(path))
					.Where(file =>
						OwnedHelperFileNameRegex().IsMatch(file.Name) &&
						string.Equals(
							file.DirectoryName,
							fullRoot,
							StringComparison.OrdinalIgnoreCase) &&
						(file.Attributes &
							(FileAttributes.Directory |
								FileAttributes.ReparsePoint)) == 0 &&
						AntigravityPrivateKeyAcl.IsPrivateFile(file.FullName))
					.OrderByDescending(file => file.LastWriteTimeUtc)
					.ThenByDescending(file => file.CreationTimeUtc)
					.ToList();

				foreach (FileInfo candidate in candidates)
				{
					if (retained.Count >= MaximumRetainedHelperGenerations)
					{
						break;
					}

					retained.Add(candidate.FullName);
				}

				foreach (FileInfo candidate in candidates)
				{
					if (retained.Contains(candidate.FullName))
					{
						continue;
					}

					RemoveHelperVerificationCacheEntry(candidate.FullName);
					TryDeleteFile(candidate.FullName);
				}
			}
		}
		catch
		{
			// Helper retirement is best-effort and never invalidates an
			// otherwise verified installation.
		}
	}

	private static bool TryGetCurrentlyConfiguredOwnedHelperPath(
		string settingsPath,
		string privateRoot,
		out string? helperPath)
	{
		helperPath = null;

		if (!TryReadSettings(settingsPath, out byte[]? settingsBytes) ||
			settingsBytes is null)
		{
			return false;
		}

		try
		{
			return TryInspectStatusLineConfiguration(
					settingsBytes,
					privateRoot,
					out StatusLineConfigurationKind configurationKind,
					out helperPath) &&
				configurationKind is (
					StatusLineConfigurationKind.OwnedEnabled or
					StatusLineConfigurationKind.OwnedDisabled) &&
				helperPath is not null;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(settingsBytes);
		}
	}

	private static bool TryDeleteOwnedArtifacts(
		string settingsPath,
		string privateRoot,
		string captureFilePath,
		string? configuredOwnedHelperPath = null)
	{
		try
		{
			if (!OperatingSystem.IsWindows() ||
				!Path.IsPathFullyQualified(privateRoot) ||
				!Path.IsPathFullyQualified(captureFilePath))
			{
				return false;
			}

			string fullRoot = Path.GetFullPath(privateRoot);
			string fullCapturePath = Path.GetFullPath(captureFilePath);

			if (!string.Equals(
					Path.GetDirectoryName(fullCapturePath),
					fullRoot,
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					Path.GetFileName(fullCapturePath),
					AntigravityStatusLineCapture.CaptureFileName,
					StringComparison.Ordinal) ||
				!AntigravityPrivateKeyAcl.IsPrivateDirectory(fullRoot))
			{
				return false;
			}

			bool cleanupSucceeded = true;

			lock (HelperPreparationLock)
			{
				if (!TrySettingsHasNoStatusLine(settingsPath, fullRoot))
				{
					// A cooperating installer cannot enter while the mutation mutex is
					// held. If an external writer restored any status line, leave every
					// artifact untouched rather than deleting an active generation.
					return false;
				}

				cleanupSucceeded &= TryDeletePrivateOwnedFile(
					fullCapturePath,
					fullRoot);
				cleanupSucceeded &= TryDeletePrivateOwnedFile(
					Path.Combine(
						fullRoot,
						AntigravityStatusLineCapture.LockFileName),
					fullRoot);

				if (!string.IsNullOrWhiteSpace(configuredOwnedHelperPath))
				{
					string fullConfiguredHelperPath = Path.GetFullPath(
						configuredOwnedHelperPath);

					if (!string.Equals(
							Path.GetDirectoryName(fullConfiguredHelperPath),
							fullRoot,
							StringComparison.OrdinalIgnoreCase) ||
						!OwnedHelperFileNameRegex().IsMatch(
							Path.GetFileName(fullConfiguredHelperPath)))
					{
						return false;
					}

					RemoveHelperVerificationCacheEntry(
						fullConfiguredHelperPath);
					cleanupSucceeded &= TryDeletePrivateOwnedFile(
						fullConfiguredHelperPath,
						fullRoot);
				}

				foreach (string helperPath in Directory.EnumerateFiles(
					fullRoot,
					"AiUsageDashboard.AntigravityCapture-*.exe",
					SearchOption.TopDirectoryOnly))
				{
					string fullHelperPath = Path.GetFullPath(helperPath);

					if (!string.Equals(
							Path.GetDirectoryName(fullHelperPath),
							fullRoot,
							StringComparison.OrdinalIgnoreCase) ||
						!OwnedHelperFileNameRegex().IsMatch(
							Path.GetFileName(fullHelperPath)))
					{
						continue;
					}

					if (!TryPrepareEnumeratedHelperForRemoval(
							fullHelperPath,
							out bool shouldDelete))
					{
						cleanupSucceeded = false;
						continue;
					}

					if (!shouldDelete)
					{
						continue;
					}

					RemoveHelperVerificationCacheEntry(fullHelperPath);
					cleanupSucceeded &= TryDeletePrivateOwnedFile(
						fullHelperPath,
						fullRoot);
				}
			}

			bool rootIsEmpty =
				!Directory.EnumerateFileSystemEntries(fullRoot).Any();
			AntigravityPrivateKeyAcl.TryDeleteEmptyCreatedDirectory(fullRoot);

			return cleanupSucceeded &&
				(!rootIsEmpty || IsPathAbsent(fullRoot));
		}
		catch
		{
			// The configuration removal is already committed. Artifact cleanup
			// remains retryable and never broadens its validated targets.
			return false;
		}
	}

	private static bool TryPrepareEnumeratedHelperForRemoval(
		string helperPath,
		out bool shouldDelete)
	{
		shouldDelete = false;

		try
		{
			if (AntigravityPrivateKeyAcl.IsPrivateFile(helperPath))
			{
				shouldDelete = true;
				return true;
			}

			if (!AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
					helperPath))
			{
				return true;
			}

			Match fileNameMatch = OwnedHelperFileNameRegex().Match(
				Path.GetFileName(helperPath));

			if (!fileNameMatch.Success)
			{
				return true;
			}

			// Older builds could leave a helper with an inherited ACL after the
			// status-line setting had already been removed. Its content-addressed
			// file name is durable ownership evidence; a lookalike with different
			// contents remains untouched.
			RemoveHelperVerificationCacheEntry(helperPath);

			if (!TryGetVerifiedHelperHash(helperPath, out byte[]? helperHash) ||
				helperHash is null)
			{
				return false;
			}

			string actualHashPrefix = Convert.ToHexString(helperHash)
				.ToLowerInvariant()[..16];

			if (!string.Equals(
					actualHashPrefix,
					fileNameMatch.Groups["hash"].Value,
					StringComparison.Ordinal))
			{
				return true;
			}

			if (!AntigravityPrivateKeyAcl.TryProtectNewFile(helperPath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(helperPath))
			{
				return false;
			}

			shouldDelete = true;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TrySettingsHasNoStatusLine(
		string settingsPath,
		string privateRoot)
	{
		if (!TryReadSettings(settingsPath, out byte[]? settingsBytes) ||
			settingsBytes is null)
		{
			return false;
		}

		try
		{
			return TryInspectStatusLineConfiguration(
					settingsBytes,
					privateRoot,
					out StatusLineConfigurationKind configurationKind,
					out _) &&
				configurationKind == StatusLineConfigurationKind.Missing;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(settingsBytes);
		}
	}

	private static bool TryDeletePrivateOwnedFile(
		string path,
		string expectedDirectory)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);

			if (!string.Equals(
					Path.GetDirectoryName(fullPath),
					expectedDirectory,
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (IsPathAbsent(fullPath))
			{
				return true;
			}

			return AntigravityPrivateKeyAcl.IsPrivateFile(fullPath) &&
				TryDeleteFile(fullPath);
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryReplaceSettingsAtomically(
		string settingsPath,
		ReadOnlySpan<byte> expectedBytes,
		ReadOnlySpan<byte> updatedBytes,
		Action? beforeCommittedSecurityValidation = null)
	{
		string? directory = Path.GetDirectoryName(settingsPath);

		if (directory is null)
		{
			return false;
		}

		string temporaryPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.tmp");
		string backupPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.bak");
		bool mayDeleteBackup = false;
		bool preserveBackup = false;
		bool replacementAttempted = false;
		bool replacementCommitted = false;
		byte[]? displacedBytes = null;
		string? displacedSddl = null;

		try
		{
			FileInfo settingsFile = new(settingsPath);
			FileSecurity originalSecurity = settingsFile.GetAccessControl(
				SettingsSecuritySections);
			string originalSddl = originalSecurity.GetSecurityDescriptorSddlForm(
				SettingsSecuritySections);

			using (FileStream output = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.WriteThrough))
			{
				output.Write(updatedBytes);
				output.Flush(flushToDisk: true);
			}

			if (!TryApplyReplacementSettingsSecurity(
					temporaryPath,
					originalSddl))
			{
				return false;
			}

			if (!TryFileContentMatches(settingsPath, expectedBytes))
			{
				return false;
			}

			replacementAttempted = true;
			File.Replace(
				temporaryPath,
				settingsPath,
				backupPath,
				ignoreMetadataErrors: false);
			replacementCommitted = true;
			if (!TryCaptureSettingsFileState(
					backupPath,
					out displacedBytes,
					out displacedSddl) ||
				displacedBytes is null ||
				displacedSddl is null)
			{
				// The displaced version cannot be proven safe to delete or restore.
				// Put it back without deleting the recovery copy; this avoids
				// leaving our stale write active even when the concurrent file is
				// too large or otherwise unavailable for a bounded snapshot.
				if (TryRestoreBackupPreservingRecoveryIfTargetMatches(
						settingsPath,
						updatedBytes,
						backupPath))
				{
					replacementCommitted = false;
				}
				preserveBackup = true;
				return false;
			}

			if (!CryptographicOperations.FixedTimeEquals(
					displacedBytes,
					expectedBytes))
			{
				// File.Replace's backup is the exact target version displaced by
				// this write. A mismatch proves that another process saved settings
				// after our optimistic read. Restore that newer version only while
				// the target is still exactly the bytes written by us.
				if (TryRestoreBackupIfTargetMatches(
						settingsPath,
						updatedBytes,
						backupPath,
						displacedBytes,
						displacedSddl,
						displacedSddl))
				{
					replacementCommitted = false;
				}
				else
				{
					preserveBackup = true;
				}

				return false;
			}

			beforeCommittedSecurityValidation?.Invoke();
			if (!TryValidateAndRepairCommittedSettingsSecurity(
					settingsPath,
					updatedBytes,
					displacedSddl))
			{
				if (TryRestoreBackupIfTargetMatches(
						settingsPath,
						updatedBytes,
						backupPath,
						displacedBytes,
						displacedSddl,
						displacedSddl))
				{
					replacementCommitted = false;
				}
				else
				{
					// The target no longer contains our write. Never roll back over
					// a third-party save; retain the displaced file for recovery.
					preserveBackup = true;
				}

				return false;
			}

			mayDeleteBackup = true;
			TryDeleteFile(backupPath);
			return true;
		}
		catch
		{
			if (replacementAttempted && File.Exists(backupPath))
			{
				if ((!File.Exists(settingsPath) &&
					TryRestoreMissingTarget(backupPath, settingsPath)) ||
					TryRestoreBackupIfTargetMatches(
						settingsPath,
						updatedBytes,
						backupPath,
						displacedBytes,
						displacedSddl,
						displacedSddl))
				{
					replacementCommitted = false;
				}
				else
				{
					preserveBackup = true;
				}
			}

			return false;
		}
		finally
		{
			if (displacedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(displacedBytes);
			}

			TryDeleteFile(temporaryPath);

			if (!preserveBackup && (mayDeleteBackup || !replacementCommitted))
			{
				TryDeleteFile(backupPath);
			}
		}
	}

	internal static bool TryValidateAndRepairCommittedSettingsSecurity(
		string settingsPath,
		ReadOnlySpan<byte> expectedBytes,
		string originalSddl,
		string? authoritativeRepairPrerequisiteSddl = null)
	{
		try
		{
			if (!Path.IsPathFullyQualified(settingsPath) ||
				string.IsNullOrWhiteSpace(originalSddl) ||
				!HasCompleteSettingsSecurityDescriptor(originalSddl) ||
				!File.Exists(settingsPath) ||
				(File.GetAttributes(settingsPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			// Keep the committed file object pinned while validating and, only if
			// needed, repairing its DACL. Denying write/delete sharing prevents a
			// path-based replacement from redirecting the security update.
			using FileStream committedFile = new FileInfo(settingsPath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.SequentialScan,
				fileSecurity: null);

			if (!TryStreamContentMatches(committedFile, expectedBytes))
			{
				return false;
			}

			if (!TryValidateAndRepairSettingsSecurityDescriptor(
					settingsPath,
					committedFile,
					originalSddl,
					authoritativeRepairPrerequisiteSddl))
			{
				return false;
			}

			return TryStreamContentMatches(committedFile, expectedBytes);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryValidateAndRepairCommittedSettingsSecurityOnly(
		string settingsPath,
		string originalSddl,
		string? authoritativeRepairPrerequisiteSddl,
		long expectedLength,
		ReadOnlySpan<byte> expectedHash)
	{
		try
		{
			if (!Path.IsPathFullyQualified(settingsPath) ||
				string.IsNullOrWhiteSpace(originalSddl) ||
				!HasCompleteSettingsSecurityDescriptor(originalSddl) ||
				!File.Exists(settingsPath) ||
				(File.GetAttributes(settingsPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream committedFile = new FileInfo(settingsPath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.None,
				fileSecurity: null);
			if (!TryStreamFingerprintMatches(
					committedFile,
					expectedLength,
					expectedHash) ||
				!TryValidateAndRepairSettingsSecurityDescriptor(
					settingsPath,
					committedFile,
					originalSddl,
					authoritativeRepairPrerequisiteSddl))
			{
				return false;
			}

			return TryStreamFingerprintMatches(
				committedFile,
				expectedLength,
				expectedHash);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryValidateAndRepairSettingsSecurityDescriptor(
		string settingsPath,
		FileStream committedFile,
		string originalSddl,
		string? authoritativeRepairPrerequisiteSddl)
	{
		string committedSddl = committedFile
			.GetAccessControl()
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);

		if (SettingsSecurityMatchesAfterReplacement(
				originalSddl,
				committedSddl))
		{
			return true;
		}

		bool canRepairLegacyExpansion =
			SettingsSecurityCanRepairLegacyExpansion(
				originalSddl,
				committedSddl);
		bool canRepairAuthoritatively =
			authoritativeRepairPrerequisiteSddl is not null &&
			(SettingsSecurityMatchesAfterReplacement(
				authoritativeRepairPrerequisiteSddl,
				committedSddl) ||
			SettingsSecurityCanRepairLegacyExpansion(
				authoritativeRepairPrerequisiteSddl,
				committedSddl));
		if (!SettingsSecurityIdentityMatches(originalSddl, committedSddl) ||
			(!canRepairLegacyExpansion && !canRepairAuthoritatively))
		{
			return false;
		}

		// Do not require WRITE_DAC when the descriptor was already accepted.
		// Open a separate repair handle only when repair is justified. The read
		// guard keeps the path pinned and denies data writers or replacement.
		using FileStream securityRepair = new FileInfo(settingsPath).Create(
			FileMode.Open,
			CommittedSettingsRepairRights,
			FileShare.Read,
			4096,
			FileOptions.None,
			fileSecurity: null);
		string repairSddl = securityRepair
			.GetAccessControl()
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
		if (!string.Equals(
				committedSddl,
				repairSddl,
				StringComparison.Ordinal))
		{
			return false;
		}

		FileSecurity replacementSecurity = new();
		replacementSecurity.SetSecurityDescriptorSddlForm(
			originalSddl,
			AccessControlSections.Access);
		securityRepair.SetAccessControl(replacementSecurity);

		committedSddl = committedFile
			.GetAccessControl()
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
		return SettingsSecurityMatchesAfterReplacement(
			originalSddl,
			committedSddl);
	}

	private static bool HasCompleteSettingsSecurityDescriptor(string sddl)
	{
		try
		{
			RawSecurityDescriptor descriptor = new(sddl);
			return descriptor.Owner is not null &&
				descriptor.Group is not null &&
				descriptor.DiscretionaryAcl is not null;
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryApplyReplacementSettingsSecurity(
		string temporaryPath,
		string originalSddl)
	{
		try
		{
			if (!Path.IsPathFullyQualified(temporaryPath) ||
				string.IsNullOrWhiteSpace(originalSddl) ||
				!File.Exists(temporaryPath) ||
				(File.GetAttributes(temporaryPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			FileInfo temporaryFile = new(temporaryPath);
			string currentSddl = temporaryFile
				.GetAccessControl(SettingsSecuritySections)
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);

			if (!SettingsSecurityIdentityMatches(originalSddl, currentSddl))
			{
				return false;
			}

			// A same-directory temporary file normally already has the destination's
			// inherited descriptor. Avoid rewriting an exact descriptor because
			// Windows can materialize legacy inherited ACLs by adding the
			// auto-inherited control flag even when every ACE remains unchanged.
			if (string.Equals(
					originalSddl,
					currentSddl,
					StringComparison.Ordinal))
			{
				return true;
			}

			// GetAccessControl returns an unmodified FileSecurity instance. Passing
			// that same instance to SetAccessControl does not persist it to another
			// file. Rehydrate only the DACL into a new descriptor so the access
			// section is explicitly marked modified without requesting owner/group
			// privileges.
			FileSecurity replacementSecurity = new();
			replacementSecurity.SetSecurityDescriptorSddlForm(
				originalSddl,
				AccessControlSections.Access);
			temporaryFile.SetAccessControl(replacementSecurity);

			// File.Replace preserves the destination DACL, but the replacement file
			// still supplies its owner and group. Require the complete descriptor to
			// match before replacement; a different owner/group fails closed rather
			// than escalating this process to rewrite either identity.
			string preparedSddl = temporaryFile
				.GetAccessControl(SettingsSecuritySections)
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			return SettingsSecurityIdentityMatches(originalSddl, preparedSddl) &&
				string.Equals(
					originalSddl,
					preparedSddl,
					StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	internal static bool SettingsSecurityMatchesAfterReplacement(
		string originalSddl,
		string committedSddl)
	{
		if (string.Equals(
				originalSddl,
				committedSddl,
				StringComparison.Ordinal))
		{
			return true;
		}

		try
		{
			RawSecurityDescriptor original = new(originalSddl);
			RawSecurityDescriptor committed = new(committedSddl);

			if (!SecurityDescriptorIdentityMatches(original, committed) ||
				original.DiscretionaryAcl is not RawAcl originalDacl ||
				committed.DiscretionaryAcl is not RawAcl committedDacl ||
				!HasInheritedAce(originalDacl))
			{
				return false;
			}

			const ControlFlags AllowedNormalization =
				ControlFlags.DiscretionaryAclAutoInherited;
			ControlFlags originalFlags = original.ControlFlags;
			ControlFlags committedFlags = committed.ControlFlags;

			if ((originalFlags & (
					AllowedNormalization |
					ControlFlags.DiscretionaryAclProtected)) != 0 ||
				committedFlags != (originalFlags | AllowedNormalization))
			{
				return false;
			}

			return RawAclBinaryEquals(originalDacl, committedDacl);
		}
		catch
		{
			return false;
		}
	}

	internal static bool SettingsSecurityCanRepairLegacyExpansion(
		string originalSddl,
		string committedSddl)
	{
		try
		{
			RawSecurityDescriptor original = new(originalSddl);
			RawSecurityDescriptor committed = new(committedSddl);
			if (!SecurityDescriptorIdentityMatches(original, committed) ||
				original.DiscretionaryAcl is not RawAcl originalDacl ||
				committed.DiscretionaryAcl is not RawAcl committedDacl ||
				originalDacl.Count == 0 ||
				originalDacl.Revision != committedDacl.Revision ||
				committedDacl.Count != checked(originalDacl.Count * 2))
			{
				return false;
			}

			const ControlFlags AllowedNormalization =
				ControlFlags.DiscretionaryAclAutoInherited;
			ControlFlags originalFlags = original.ControlFlags;
			if ((originalFlags & (
					AllowedNormalization |
					ControlFlags.DiscretionaryAclProtected)) != 0 ||
				committed.ControlFlags !=
					(originalFlags | AllowedNormalization))
			{
				return false;
			}

			for (int index = 0; index < originalDacl.Count; index++)
			{
				if ((originalDacl[index].AceFlags & AceFlags.Inherited) == 0)
				{
					return false;
				}
			}

			Dictionary<string, int> expectedAceCounts = new(
				StringComparer.Ordinal);
			for (int index = 0; index < originalDacl.Count; index++)
			{
				string key = GetAceKeyIgnoringInherited(originalDacl[index]);
				expectedAceCounts.TryGetValue(key, out int count);
				expectedAceCounts[key] = checked(count + 2);
			}

			for (int index = 0; index < committedDacl.Count; index++)
			{
				string key = GetAceKeyIgnoringInherited(committedDacl[index]);
				if (!expectedAceCounts.TryGetValue(key, out int count) ||
					count == 0)
				{
					return false;
				}

				expectedAceCounts[key] = count - 1;
			}

			return expectedAceCounts.Values.All(count => count == 0);
		}
		catch
		{
			return false;
		}
	}

	private static bool SettingsSecurityIdentityMatches(
		string expectedSddl,
		string actualSddl)
	{
		try
		{
			return SecurityDescriptorIdentityMatches(
				new RawSecurityDescriptor(expectedSddl),
				new RawSecurityDescriptor(actualSddl));
		}
		catch
		{
			return false;
		}
	}

	private static bool SecurityDescriptorIdentityMatches(
		RawSecurityDescriptor expected,
		RawSecurityDescriptor actual)
	{
		return expected.Owner is not null &&
			actual.Owner is not null &&
			expected.Owner.Equals(actual.Owner) &&
			expected.Group is not null &&
			actual.Group is not null &&
			expected.Group.Equals(actual.Group);
	}

	private static bool HasInheritedAce(RawAcl dacl)
	{
		for (int index = 0; index < dacl.Count; index++)
		{
			if ((dacl[index].AceFlags & AceFlags.Inherited) != 0)
			{
				return true;
			}
		}

		return false;
	}

	private static bool RawAclBinaryEquals(RawAcl expected, RawAcl actual)
	{
		if (expected.BinaryLength != actual.BinaryLength)
		{
			return false;
		}

		byte[] expectedBytes = new byte[expected.BinaryLength];
		byte[] actualBytes = new byte[actual.BinaryLength];
		expected.GetBinaryForm(expectedBytes, 0);
		actual.GetBinaryForm(actualBytes, 0);
		return expectedBytes.AsSpan().SequenceEqual(actualBytes);
	}

	private static string GetAceKeyIgnoringInherited(GenericAce ace)
	{
		byte[] bytes = new byte[ace.BinaryLength];
		ace.GetBinaryForm(bytes, 0);
		bytes[1] &= unchecked((byte)~(byte)AceFlags.Inherited);
		return Convert.ToHexString(bytes);
	}

	private static bool TryFileContentMatches(
		string filePath,
		ReadOnlySpan<byte> expectedBytes)
	{
		try
		{
			if (!Path.IsPathFullyQualified(filePath) ||
				!File.Exists(filePath) ||
				(File.GetAttributes(filePath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream stream = new FileInfo(filePath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.SequentialScan,
				fileSecurity: null);
			return TryStreamContentMatches(stream, expectedBytes);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryStreamContentMatches(
		FileStream stream,
		ReadOnlySpan<byte> expectedBytes)
	{
		byte[]? bytes = null;

		try
		{
			if (!stream.CanRead || stream.Length != expectedBytes.Length)
			{
				return false;
			}

			stream.Position = 0;
			bytes = new byte[expectedBytes.Length];
			stream.ReadExactly(bytes);
			return CryptographicOperations.FixedTimeEquals(bytes, expectedBytes);
		}
		catch
		{
			return false;
		}
		finally
		{
			if (bytes is not null)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}
		}
	}

	private static bool TryStreamFingerprintMatches(
		FileStream stream,
		long expectedLength,
		ReadOnlySpan<byte> expectedHash)
	{
		byte[]? actualHash = null;

		try
		{
			if (!stream.CanRead ||
				expectedLength <= 0 ||
				expectedLength > MaximumRollbackFingerprintBytes ||
				stream.Length != expectedLength ||
				expectedHash.Length != SHA256.HashSizeInBytes)
			{
				return false;
			}

			stream.Position = 0;
			actualHash = SHA256.HashData(stream);
			return CryptographicOperations.FixedTimeEquals(
				actualHash,
				expectedHash);
		}
		catch
		{
			return false;
		}
		finally
		{
			if (actualHash is not null)
			{
				CryptographicOperations.ZeroMemory(actualHash);
			}
		}
	}

	private static bool TryCaptureSettingsFileState(
		string filePath,
		out byte[]? bytes,
		out string? securitySddl)
	{
		bytes = null;
		securitySddl = null;

		try
		{
			if (!Path.IsPathFullyQualified(filePath) ||
				!File.Exists(filePath) ||
				(File.GetAttributes(filePath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream stream = new FileInfo(filePath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.SequentialScan,
				fileSecurity: null);
			if (stream.Length <= 0 || stream.Length > MaximumSettingsBytes)
			{
				return false;
			}

			bytes = new byte[checked((int)stream.Length)];
			stream.ReadExactly(bytes);
			string capturedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (!HasCompleteSettingsSecurityDescriptor(capturedSddl) ||
				!TryStreamContentMatches(stream, bytes))
			{
				CryptographicOperations.ZeroMemory(bytes);
				bytes = null;
				securitySddl = null;
				return false;
			}

			string confirmedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (!string.Equals(
					capturedSddl,
					confirmedSddl,
					StringComparison.Ordinal))
			{
				CryptographicOperations.ZeroMemory(bytes);
				bytes = null;
				return false;
			}

			securitySddl = capturedSddl;

			return true;
		}
		catch
		{
			if (bytes is not null)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}

			bytes = null;
			securitySddl = null;
			return false;
		}
	}

	private static bool TryCaptureSettingsFileSecurityAndFingerprint(
		string filePath,
		out string? securitySddl,
		out long length,
		out byte[]? hash)
	{
		securitySddl = null;
		length = 0;
		hash = null;

		try
		{
			if (!Path.IsPathFullyQualified(filePath) ||
				!File.Exists(filePath) ||
				(File.GetAttributes(filePath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream stream = new FileInfo(filePath).Create(
				FileMode.Open,
				SettingsStateReadRights,
				FileShare.Read,
				4096,
				FileOptions.None,
				fileSecurity: null);
			long capturedLength = stream.Length;
			if (capturedLength <= 0)
			{
				return false;
			}

			string capturedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (capturedLength <= MaximumRollbackFingerprintBytes)
			{
				stream.Position = 0;
				hash = SHA256.HashData(stream);
			}
			string confirmedSddl = stream
				.GetAccessControl()
				.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
			if (stream.Length != capturedLength ||
				!HasCompleteSettingsSecurityDescriptor(capturedSddl) ||
				!string.Equals(
					capturedSddl,
					confirmedSddl,
					StringComparison.Ordinal))
			{
				if (hash is not null)
				{
					CryptographicOperations.ZeroMemory(hash);
					hash = null;
				}
				length = 0;
				return false;
			}

			securitySddl = capturedSddl;
			length = capturedLength;
			return true;
		}
		catch
		{
			if (hash is not null)
			{
				CryptographicOperations.ZeroMemory(hash);
			}

			hash = null;
			length = 0;
			return false;
		}
	}

	internal static bool TryRestoreBackupIfTargetMatches(
		string settingsPath,
		ReadOnlySpan<byte> expectedCurrentBytes,
		string backupPath,
		string? expectedCurrentSddl = null,
		Action? beforeReplacement = null)
	{
		byte[]? expectedRestoredBytes = null;

		try
		{
			return TryCaptureSettingsFileState(
					backupPath,
					out expectedRestoredBytes,
					out string? expectedRestoredSddl) &&
				expectedRestoredBytes is not null &&
				expectedRestoredSddl is not null &&
				TryRestoreBackupIfTargetMatches(
					settingsPath,
					expectedCurrentBytes,
					backupPath,
					expectedRestoredBytes,
					expectedRestoredSddl,
					expectedCurrentSddl,
					beforeReplacement);
		}
		finally
		{
			if (expectedRestoredBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRestoredBytes);
			}
		}
	}

	internal static bool TryRestoreBackupPreservingRecoveryIfTargetMatches(
		string settingsPath,
		ReadOnlySpan<byte> expectedCurrentBytes,
		string backupPath,
		Action? beforeRestoredSecurityValidation = null)
	{
		string? directory = Path.GetDirectoryName(settingsPath);
		if (directory is null)
		{
			return false;
		}

		string recoveryPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.recovery.bak");

		byte[]? expectedRecoveryBytes = null;
		byte[]? actualRecoveryBytes = null;
		byte[]? expectedRestoredHash = null;

		try
		{
			if (!File.Exists(backupPath) ||
				!File.Exists(settingsPath) ||
				!TryCaptureSettingsFileSecurityAndFingerprint(
					backupPath,
					out string? expectedRestoredSddl,
					out long expectedRestoredLength,
					out expectedRestoredHash) ||
				expectedRestoredSddl is null ||
				!TryCaptureSettingsFileState(
					settingsPath,
					out expectedRecoveryBytes,
					out string? expectedRecoverySddl) ||
				expectedRecoveryBytes is null ||
				expectedRecoverySddl is null ||
				!CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					expectedCurrentBytes))
			{
				return false;
			}

			bool capturedStateIsKnownWrite =
				SettingsSecurityMatchesAfterReplacement(
					expectedRestoredSddl,
					expectedRecoverySddl) ||
				SettingsSecurityCanRepairLegacyExpansion(
					expectedRestoredSddl,
					expectedRecoverySddl);

			File.Replace(
				backupPath,
				settingsPath,
				destinationBackupFileName: recoveryPath,
				ignoreMetadataErrors: false);
			bool recoveryIsKnownWrite = capturedStateIsKnownWrite &&
				TryCaptureSettingsFileState(
					recoveryPath,
					out actualRecoveryBytes,
					out string? actualRecoverySddl) &&
				actualRecoveryBytes is not null &&
				actualRecoverySddl is not null &&
				CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					actualRecoveryBytes) &&
				string.Equals(
					expectedRecoverySddl,
					actualRecoverySddl,
					StringComparison.Ordinal);
			beforeRestoredSecurityValidation?.Invoke();
			bool restoredSecurityMatches = expectedRestoredHash is not null &&
				TryValidateAndRepairCommittedSettingsSecurityOnly(
					settingsPath,
					expectedRestoredSddl,
					authoritativeRepairPrerequisiteSddl:
						recoveryIsKnownWrite ? expectedRecoverySddl : null,
					expectedRestoredLength,
					expectedRestoredHash);
			return File.Exists(settingsPath) &&
				File.Exists(recoveryPath) &&
				restoredSecurityMatches;
		}
		catch
		{
			// The backup or recovery copy remains available whenever Windows
			// completed any part of the replacement.
			return false;
		}
		finally
		{
			if (expectedRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRecoveryBytes);
			}
			if (actualRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(actualRecoveryBytes);
			}
			if (expectedRestoredHash is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRestoredHash);
			}
		}
	}

	private static bool TryRestoreBackupIfTargetMatches(
		string settingsPath,
		ReadOnlySpan<byte> expectedCurrentBytes,
		string backupPath,
		byte[]? expectedRestoredBytes,
		string? expectedRestoredSddl,
		string? expectedCurrentSddl,
		Action? beforeReplacement = null)
	{
		string? directory = Path.GetDirectoryName(settingsPath);

		if (directory is null ||
			expectedRestoredBytes is null ||
			expectedRestoredSddl is null)
		{
			return false;
		}

		string recoveryPath = Path.Combine(
			directory,
			$".ai-usage-statusline-{Guid.NewGuid():N}.recovery.bak");
		bool recoveryIsKnownWrite = false;
		bool restoredStateMatches = false;
		bool capturedStateIsKnownWrite = false;
		byte[]? expectedRecoveryBytes = null;
		byte[]? actualRecoveryBytes = null;

		try
		{
			if (!File.Exists(backupPath) ||
				!File.Exists(settingsPath) ||
				!TryCaptureSettingsFileState(
					settingsPath,
					out expectedRecoveryBytes,
					out string? expectedRecoverySddl) ||
				expectedRecoveryBytes is null ||
				expectedRecoverySddl is null ||
				!CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					expectedCurrentBytes))
			{
				return false;
			}

			capturedStateIsKnownWrite = expectedCurrentSddl is not null &&
				(SettingsSecurityMatchesAfterReplacement(
					expectedCurrentSddl,
					expectedRecoverySddl) ||
				SettingsSecurityCanRepairLegacyExpansion(
					expectedCurrentSddl,
					expectedRecoverySddl));

			beforeReplacement?.Invoke();
			File.Replace(
				backupPath,
				settingsPath,
				destinationBackupFileName: recoveryPath,
				ignoreMetadataErrors: false);

			// File.Replace's recovery file is the exact destination displaced by
			// the rollback. It is disposable only when it is provably our write;
			// otherwise it may be a concurrent save and must remain recoverable.
			recoveryIsKnownWrite = capturedStateIsKnownWrite &&
				TryCaptureSettingsFileState(
					recoveryPath,
					out actualRecoveryBytes,
					out string? actualRecoverySddl) &&
				actualRecoveryBytes is not null &&
				actualRecoverySddl is not null &&
				CryptographicOperations.FixedTimeEquals(
					expectedRecoveryBytes,
					actualRecoveryBytes) &&
				string.Equals(
					expectedRecoverySddl,
					actualRecoverySddl,
					StringComparison.Ordinal);
			restoredStateMatches = TryValidateAndRepairCommittedSettingsSecurity(
				settingsPath,
				expectedRestoredBytes,
				expectedRestoredSddl,
				authoritativeRepairPrerequisiteSddl:
					recoveryIsKnownWrite ? expectedRecoverySddl : null);
			return restoredStateMatches;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (expectedRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(expectedRecoveryBytes);
			}
			if (actualRecoveryBytes is not null)
			{
				CryptographicOperations.ZeroMemory(actualRecoveryBytes);
			}

			if (recoveryIsKnownWrite && restoredStateMatches)
			{
				TryDeleteFile(recoveryPath);
			}
		}
	}

	private static bool TryRestoreMissingTarget(
		string backupPath,
		string settingsPath)
	{
		try
		{
			if (!File.Exists(backupPath) || File.Exists(settingsPath))
			{
				return false;
			}

			File.Move(backupPath, settingsPath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryReadSettings(
		string settingsPath,
		out byte[]? bytes)
	{
		bytes = null;

		try
		{
			if (!Path.IsPathFullyQualified(settingsPath) ||
				!File.Exists(settingsPath) ||
				(File.GetAttributes(settingsPath) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			using FileStream stream = new(
				settingsPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);

			if ((stream.Length <= 0) || (stream.Length > MaximumSettingsBytes))
			{
				return false;
			}

			bytes = new byte[checked((int)stream.Length)];
			stream.ReadExactly(bytes);
			_ = StrictUtf8.GetString(bytes);
			return true;
		}
		catch
		{
			if (bytes is not null)
			{
				CryptographicOperations.ZeroMemory(bytes);
			}

			bytes = null;
			return false;
		}
	}

	private static bool TryGetOwnedHelperPath(
		string? command,
		string privateRoot,
		out string? helperPath)
	{
		helperPath = null;

		if (string.IsNullOrEmpty(command) ||
			!command.EndsWith(
				$" {OwnershipMarker}",
				StringComparison.Ordinal))
		{
			return false;
		}

		string commandPath = command[..^(OwnershipMarker.Length + 1)];
		string candidate;
		bool isQuotedPath;

		if (commandPath.StartsWith("call \"", StringComparison.Ordinal) &&
			commandPath.EndsWith('"'))
		{
			candidate = commandPath[6..^1];
			isQuotedPath = true;

			if (candidate.Length == 0 || candidate.Contains('"'))
			{
				return false;
			}
		}
		else if (commandPath.Length >= 2 &&
			commandPath[0] == '"' &&
			commandPath[^1] == '"')
		{
			candidate = commandPath[1..^1];
			isQuotedPath = true;

			if (candidate.Contains('"'))
			{
				return false;
			}
		}
		else
		{
			candidate = commandPath;
			isQuotedPath = false;
		}

		if (isQuotedPath
			? !IsSafeQuotedCommandPath(candidate)
			: !IsSafeUnquotedCommandPath(candidate))
		{
			return false;
		}

		try
		{
			string fullCandidate = Path.GetFullPath(candidate);
			string fullRoot = Path.GetFullPath(privateRoot);
			string? candidateDirectory = Path.GetDirectoryName(fullCandidate);

			if (!string.Equals(
					candidateDirectory,
					fullRoot,
					StringComparison.OrdinalIgnoreCase))
			{
				if (!TryGetVerifiedLongPath(
						fullCandidate,
						out string? expandedCandidate) ||
					expandedCandidate is null)
				{
					return false;
				}

				fullCandidate = expandedCandidate;
				candidateDirectory = Path.GetDirectoryName(fullCandidate);
			}

			if (!string.Equals(
					candidateDirectory,
					fullRoot,
					StringComparison.OrdinalIgnoreCase) ||
				!OwnedHelperFileNameRegex().IsMatch(
					Path.GetFileName(fullCandidate)))
			{
				return false;
			}

			helperPath = fullCandidate;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsSafeQuotedCommandPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		foreach (char character in path)
		{
			if (char.IsControl(character) ||
				character is '"' or '%' or '!' or '$' or '`' or '&' or '|' or
					'<' or '>' or '^' or ';')
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsSafeUnquotedCommandPath(string path)
	{
		return IsSafeQuotedCommandPath(path) &&
			!path.Any(character =>
				char.IsWhiteSpace(character) ||
				character is '(' or ')' or ',' or '=');
	}

	private static bool TryGetVerifiedShortPath(
		string path,
		out string? shortPath)
	{
		shortPath = null;

		try
		{
			if (!OperatingSystem.IsWindows() ||
				!Path.IsPathFullyQualified(path) ||
				!File.Exists(path) ||
				(File.GetAttributes(path) &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			StringBuilder buffer = new(MaximumNativePathCharacters);
			uint written = GetShortPathName(
				Path.GetFullPath(path),
				buffer,
				checked((uint)buffer.Capacity));

			if (written == 0 ||
				written >= checked((uint)buffer.Capacity) ||
				!Path.IsPathFullyQualified(buffer.ToString()) ||
				!IsSafeUnquotedCommandPath(buffer.ToString()) ||
				!TryGetVerifiedLongPath(
					buffer.ToString(),
					out string? expandedPath) ||
				expandedPath is null ||
				!string.Equals(
					expandedPath,
					Path.GetFullPath(path),
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			shortPath = Path.GetFullPath(buffer.ToString());
			return true;
		}
		catch
		{
			shortPath = null;
			return false;
		}
	}

	private static bool TryGetVerifiedLongPath(
		string path,
		out string? longPath)
	{
		longPath = null;

		try
		{
			if (!OperatingSystem.IsWindows() ||
				!Path.IsPathFullyQualified(path) ||
				!File.Exists(path))
			{
				return false;
			}

			StringBuilder buffer = new(MaximumNativePathCharacters);
			uint written = GetLongPathName(
				Path.GetFullPath(path),
				buffer,
				checked((uint)buffer.Capacity));

			if (written == 0 ||
				written >= checked((uint)buffer.Capacity))
			{
				return false;
			}

			longPath = Path.GetFullPath(buffer.ToString());
			return Path.IsPathFullyQualified(longPath);
		}
		catch
		{
			longPath = null;
			return false;
		}
	}

	private static string? TryGetDefaultSettingsPath()
	{
		string userProfile = Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile);

		return string.IsNullOrWhiteSpace(userProfile)
			? null
			: Path.Combine(
				userProfile,
				".gemini",
				"antigravity-cli",
				"settings.json");
	}

	private static bool TryDeleteFile(string path)
	{
		try
		{
			if (IsPathAbsent(path))
			{
				return true;
			}

			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			{
				return false;
			}

			File.Delete(path);
			return IsPathAbsent(path);
		}
		catch
		{
			// Best-effort cleanup never changes the fail-closed result.
			return false;
		}
	}

	private static bool IsPathAbsent(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return false;
		}
		catch (FileNotFoundException)
		{
			return true;
		}
		catch (DirectoryNotFoundException)
		{
			return true;
		}
		catch
		{
			return false;
		}
	}

	[DllImport(
		"kernel32.dll",
		EntryPoint = "GetShortPathNameW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static extern uint GetShortPathName(
		string longPath,
		StringBuilder shortPath,
		uint shortPathLength);

	[DllImport(
		"kernel32.dll",
		EntryPoint = "GetLongPathNameW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static extern uint GetLongPathName(
		string shortPath,
		StringBuilder longPath,
		uint longPathLength);

	[GeneratedRegex(
		"^AiUsageDashboard\\.AntigravityCapture-(?<hash>[0-9a-f]{16})\\.exe$",
		RegexOptions.CultureInvariant)]
	private static partial Regex OwnedHelperFileNameRegex();
}
