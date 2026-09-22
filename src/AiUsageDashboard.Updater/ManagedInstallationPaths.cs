using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal static class ManagedInstallationPaths
{
	internal const string MaintenanceUpdaterGenerationPrefix =
		"AiUsageDashboard.Updater.";
	internal const string MaintenanceUpdaterFileName =
		MaintenanceUpdaterPathContract.ApplicationFileName;
	internal const string UninstallReceiptFileName =
		".uninstalling-v1.json";

	internal static string GetCurrentDirectory(string installRoot)
	{
		return Path.Combine(NormalizeDirectory(installRoot), "current");
	}

	internal static string GetInstalledAppExecutable(string installRoot)
	{
		return Path.Combine(
			GetCurrentDirectory(installRoot),
			"app",
			"AiUsageDashboard.App.exe");
	}

	internal static string GetInstalledManifest(string installRoot)
	{
		return Path.Combine(
			GetCurrentDirectory(installRoot),
			UpdateManifest.InstalledFileName);
	}

	internal static string GetMaintenanceUpdater(string maintenanceRoot)
	{
		return Path.Combine(
			NormalizeDirectory(maintenanceRoot),
			MaintenanceUpdaterFileName);
	}

	internal static string GetMaintenanceUpdaterGeneration(
		string maintenanceRoot,
		string sha256)
	{
		string normalizedHash = NormalizeSha256(sha256);

		return Path.Combine(
			NormalizeDirectory(maintenanceRoot),
			$"{MaintenanceUpdaterGenerationPrefix}{normalizedHash}.exe");
	}

	internal static string GetMaintenanceUpdaterRemovalGeneration(
		string maintenanceRoot,
		string sha256,
		string generationId)
	{
		string normalizedHash = NormalizeSha256(sha256);
		ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
		string normalizedGenerationId = generationId.ToLowerInvariant();

		if ((normalizedGenerationId.Length != 32) ||
			normalizedGenerationId.Any(character =>
				!IsLowerHexadecimal(character)))
		{
			throw new ArgumentException(
				"Maintenance updater generation ID must contain 32 hexadecimal " +
					"characters.",
				nameof(generationId));
		}

		return Path.Combine(
			NormalizeDirectory(maintenanceRoot),
			$"{MaintenanceUpdaterGenerationPrefix}{normalizedHash}." +
				$"{normalizedGenerationId}.exe");
	}

	internal static bool TryGetMaintenanceUpdaterGenerationHash(
		string fileName,
		out string generationHash)
	{
		generationHash = string.Empty;
		const string suffix = ".exe";
		int legacyLength = MaintenanceUpdaterGenerationPrefix.Length +
			64 + suffix.Length;
		int removalLength = legacyLength + 1 + 32;

		if (((fileName.Length != legacyLength) &&
				(fileName.Length != removalLength)) ||
			!fileName.StartsWith(
				MaintenanceUpdaterGenerationPrefix,
				StringComparison.Ordinal) ||
			!fileName.EndsWith(suffix, StringComparison.Ordinal))
		{
			return false;
		}

		string candidate = fileName.Substring(
			MaintenanceUpdaterGenerationPrefix.Length,
			64);

		if (candidate.Any(character => !IsLowerHexadecimal(character)))
		{
			return false;
		}

		if (fileName.Length == removalLength)
		{
			int generationSeparatorIndex =
				MaintenanceUpdaterGenerationPrefix.Length + 64;
			string generationId = fileName.Substring(
				generationSeparatorIndex + 1,
				32);

			if ((fileName[generationSeparatorIndex] != '.') ||
				generationId.Any(character =>
					!IsLowerHexadecimal(character)))
			{
				return false;
			}
		}

		generationHash = candidate;
		return true;
	}

	internal static string GetUninstallReceipt(string installRoot)
	{
		return Path.Combine(
			NormalizeDirectory(installRoot),
			UninstallReceiptFileName);
	}

	internal static void EnsureDistinctRoots(
		string installRoot,
		string maintenanceRoot,
		string userDataRoot)
	{
		string normalizedInstallRoot = NormalizeDirectory(installRoot);
		string normalizedMaintenanceRoot = NormalizeDirectory(maintenanceRoot);
		string normalizedUserDataRoot = NormalizeDirectory(userDataRoot);

		if (DirectoriesOverlap(
				normalizedInstallRoot,
				normalizedMaintenanceRoot) ||
			DirectoriesOverlap(normalizedInstallRoot, normalizedUserDataRoot) ||
			DirectoriesOverlap(normalizedMaintenanceRoot, normalizedUserDataRoot))
		{
			throw new InvalidOperationException(
				"The install, maintenance, and user-data roots must not overlap.");
		}
	}

	internal static bool IsPathInsideDirectory(
		string candidatePath,
		string directoryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
		string normalizedCandidate = Path.GetFullPath(candidatePath);
		string normalizedDirectory = NormalizeDirectory(directoryPath);
		string directoryPrefix = normalizedDirectory +
			Path.DirectorySeparatorChar;
		return normalizedCandidate.StartsWith(
			directoryPrefix,
			StringComparison.OrdinalIgnoreCase);
	}

	internal static string NormalizeDirectory(string directoryPath)
	{
		return UpdateTransaction.NormalizeInstallRoot(directoryPath);
	}

	private static bool DirectoriesOverlap(string firstPath, string secondPath)
	{
		return string.Equals(
				firstPath,
				secondPath,
				StringComparison.OrdinalIgnoreCase) ||
			IsPathInsideDirectory(firstPath, secondPath) ||
			IsPathInsideDirectory(secondPath, firstPath);
	}

	private static bool IsLowerHexadecimal(char character)
	{
		return ((character >= '0') && (character <= '9')) ||
			((character >= 'a') && (character <= 'f'));
	}

	internal static string NormalizeSha256(string sha256)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
		string normalizedHash = sha256.ToLowerInvariant();

		if ((normalizedHash.Length != 64) ||
			normalizedHash.Any(character => !IsLowerHexadecimal(character)))
		{
			throw new ArgumentException(
				"Maintenance updater SHA-256 must contain 64 hexadecimal characters.",
				nameof(sha256));
		}

		return normalizedHash;
	}
}
