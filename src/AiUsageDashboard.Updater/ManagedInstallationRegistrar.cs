using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class ManagedInstallationRegistrar
{
	private readonly IInstalledApplicationRegistrationStore _registrationStore;

	internal ManagedInstallationRegistrar(
		IInstalledApplicationRegistrationStore registrationStore)
	{
		_registrationStore = registrationStore ??
			throw new ArgumentNullException(nameof(registrationStore));
	}

	internal async Task EnsureRegisteredAsync(
		string runningUpdaterExecutablePath,
		string installRoot,
		string maintenanceRoot,
		string userDataRoot,
		UpdateManifest expectedManifest,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runningUpdaterExecutablePath);
		ArgumentNullException.ThrowIfNull(expectedManifest);
		expectedManifest.Validate();
		ManagedInstallationPaths.EnsureDistinctRoots(
			installRoot,
			maintenanceRoot,
			userDataRoot);
		string normalizedInstallRoot =
			ManagedInstallationPaths.NormalizeDirectory(installRoot);
		string installedManifestPath =
			ManagedInstallationPaths.GetInstalledManifest(normalizedInstallRoot);
		string installedAppPath =
			ManagedInstallationPaths.GetInstalledAppExecutable(normalizedInstallRoot);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(
			normalizedInstallRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			normalizedInstallRoot,
			mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			installedManifestPath,
			mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			installedAppPath,
			mustExist: true);
		UpdateManifest installedManifest = await UpdateManifest.ReadAsync(
			installedManifestPath,
			cancellationToken);

		if (!string.Equals(
				installedManifest.Version,
				expectedManifest.Version,
				StringComparison.Ordinal) ||
			!string.Equals(
				installedManifest.PayloadGenerationId,
				expectedManifest.PayloadGenerationId,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The installed manifest changed before Windows registration.");
		}

		string maintenanceUpdaterPath = await EnsureMaintenanceUpdaterAsync(
			runningUpdaterExecutablePath,
			maintenanceRoot,
			cancellationToken);
		string quietUninstallCommand = string.Join(
			" ",
			QuoteCommandLineArgument(maintenanceUpdaterPath),
			"uninstall",
			"--confirm",
			"--install-root",
			QuoteCommandLineArgument(normalizedInstallRoot));
		string uninstallCommand = string.Join(
			" ",
			QuoteCommandLineArgument(maintenanceUpdaterPath),
			"uninstall",
			"--confirm",
			"--notify",
			"--install-root",
			QuoteCommandLineArgument(normalizedInstallRoot));
		_registrationStore.Upsert(new InstalledApplicationRegistration(
			installedManifest.Version,
			normalizedInstallRoot,
			$"{QuoteCommandLineArgument(installedAppPath)},0",
			uninstallCommand,
			quietUninstallCommand,
			DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
		DeleteStaleUninstallReceipt(normalizedInstallRoot);
	}

	private static void DeleteStaleUninstallReceipt(string installRoot)
	{
		string receiptPath = ManagedInstallationPaths.GetUninstallReceipt(
			installRoot);

		if (!File.Exists(receiptPath))
		{
			return;
		}

		UpdateTransaction.ThrowIfNotOrdinaryFile(
			receiptPath,
			mustExist: true);
		File.Delete(receiptPath);
	}

	private static async Task<string> EnsureMaintenanceUpdaterAsync(
		string sourceExecutablePath,
		string maintenanceRoot,
		CancellationToken cancellationToken)
	{
		string normalizedSourcePath = Path.GetFullPath(sourceExecutablePath);
		string normalizedMaintenanceRoot =
			ManagedInstallationPaths.NormalizeDirectory(maintenanceRoot);
		string destinationPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			normalizedMaintenanceRoot);
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			normalizedSourcePath,
			mustExist: true);
		FileVersionInfo sourceVersion = FileVersionInfo.GetVersionInfo(
			normalizedSourcePath);

		if (!string.Equals(
				sourceVersion.ProductName,
				"AiUsageDashboard.Updater",
				StringComparison.Ordinal) ||
			!string.Equals(
				Path.GetExtension(normalizedSourcePath),
				".exe",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"The running process is not a standalone AI Usage updater.");
		}

		EnsureMaintenanceDirectory(normalizedMaintenanceRoot);

		if (string.Equals(
				normalizedSourcePath,
				destinationPath,
				StringComparison.OrdinalIgnoreCase))
		{
			return destinationPath;
		}

		string sourceHash = await GetSha256Async(
			normalizedSourcePath,
			cancellationToken);

		string temporaryPath = destinationPath + "." +
			Guid.NewGuid().ToString("N") + ".installing";
		string installedUpdaterPath = destinationPath;

		try
		{
			UpdateTransaction.ThrowIfNotOrdinaryFile(
				temporaryPath,
				mustExist: false);
			File.Copy(normalizedSourcePath, temporaryPath, overwrite: false);
			await EnsureFilesMatchAsync(
				normalizedSourcePath,
				temporaryPath,
				cancellationToken);
			UpdateTransaction.ThrowIfNotOrdinaryFile(
				destinationPath,
				mustExist: false);

			try
			{
				File.Move(temporaryPath, destinationPath, overwrite: true);
			}
			catch (IOException) when (File.Exists(destinationPath))
			{
				installedUpdaterPath = await RetainCurrentGenerationAsync(
					normalizedSourcePath,
					sourceHash,
					temporaryPath,
					destinationPath,
					normalizedMaintenanceRoot,
					cancellationToken);
			}
			catch (UnauthorizedAccessException) when (File.Exists(destinationPath))
			{
				installedUpdaterPath = await RetainCurrentGenerationAsync(
					normalizedSourcePath,
					sourceHash,
					temporaryPath,
					destinationPath,
					normalizedMaintenanceRoot,
					cancellationToken);
			}
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				UpdateTransaction.ThrowIfNotOrdinaryFile(
					temporaryPath,
					mustExist: true);
				File.Delete(temporaryPath);
			}
		}

		EnsureExistingMaintenanceUpdaterIsCompatible(installedUpdaterPath);
		await EnsureFilesMatchAsync(
			normalizedSourcePath,
			installedUpdaterPath,
			cancellationToken);
		await TryDeleteStaleGenerationUpdatersAsync(
			normalizedMaintenanceRoot,
			installedUpdaterPath,
			normalizedSourcePath,
			cancellationToken);
		return installedUpdaterPath;
	}

	private static async Task<string> RetainCurrentGenerationAsync(
		string sourcePath,
		string sourceHash,
		string temporaryPath,
		string destinationPath,
		string maintenanceRoot,
		CancellationToken cancellationToken)
	{
		EnsureExistingMaintenanceUpdaterIsCompatible(destinationPath);

		try
		{
			await EnsureFilesMatchAsync(
				sourcePath,
				destinationPath,
				cancellationToken);
			return destinationPath;
		}
		catch (InvalidDataException)
		{
			string generationPath =
				ManagedInstallationPaths.GetMaintenanceUpdaterGeneration(
					maintenanceRoot,
					sourceHash);
			UpdateTransaction.ThrowIfNotOrdinaryFile(
				generationPath,
				mustExist: false);

			if (!File.Exists(generationPath))
			{
				File.Move(temporaryPath, generationPath, overwrite: false);
			}

			EnsureExistingMaintenanceUpdaterIsCompatible(generationPath);
			await EnsureFilesMatchAsync(
				sourcePath,
				generationPath,
				cancellationToken);
			return generationPath;
		}
	}

	private static async Task TryDeleteStaleGenerationUpdatersAsync(
		string maintenanceRoot,
		string retainedUpdaterPath,
		string runningUpdaterPath,
		CancellationToken cancellationToken)
	{
		foreach (FileInfo file in new DirectoryInfo(maintenanceRoot)
			.GetFiles($"{ManagedInstallationPaths.MaintenanceUpdaterGenerationPrefix}*.exe"))
		{
			if (string.Equals(
					file.FullName,
					retainedUpdaterPath,
					StringComparison.OrdinalIgnoreCase) ||
				string.Equals(
					file.FullName,
					runningUpdaterPath,
					StringComparison.OrdinalIgnoreCase) ||
				!ManagedInstallationPaths.TryGetMaintenanceUpdaterGenerationHash(
					file.Name,
					out string expectedHash))
			{
				continue;
			}

			try
			{
				UpdateTransaction.ThrowIfNotOrdinaryFile(
					file.FullName,
					mustExist: true);
				EnsureExistingMaintenanceUpdaterIsCompatible(file.FullName);
				string actualHash = await GetSha256Async(
					file.FullName,
					cancellationToken);

				if (!string.Equals(
						actualHash,
						expectedHash,
						StringComparison.Ordinal))
				{
					continue;
				}

				File.Delete(file.FullName);
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException or
					InvalidDataException)
			{
				// 執行中的舊 generation 交由之後更新或解除安裝清理。
			}
		}
	}

	private static void EnsureExistingMaintenanceUpdaterIsCompatible(
		string executablePath)
	{
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			executablePath,
			mustExist: true);
		FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);

		if (!string.Equals(
				versionInfo.ProductName,
				"AiUsageDashboard.Updater",
				StringComparison.Ordinal) ||
			string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
		{
			throw new InvalidDataException(
				"The installed maintenance updater identity is invalid.");
		}
	}

	private static void EnsureMaintenanceDirectory(string maintenanceRoot)
	{
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(maintenanceRoot);
		Directory.CreateDirectory(maintenanceRoot);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(maintenanceRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			maintenanceRoot,
			mustExist: true);
	}

	private static async Task EnsureFilesMatchAsync(
		string firstPath,
		string secondPath,
		CancellationToken cancellationToken)
	{
		FileInfo first = new(firstPath);
		FileInfo second = new(secondPath);

		if (first.Length != second.Length)
		{
			throw new InvalidDataException(
				"The maintenance updater copy size is invalid.");
		}

		await using FileStream firstStream = new(
			firstPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		await using FileStream secondStream = new(
			secondPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] firstHash = await SHA256.HashDataAsync(
			firstStream,
			cancellationToken);
		byte[] secondHash = await SHA256.HashDataAsync(
			secondStream,
			cancellationToken);

		if (!CryptographicOperations.FixedTimeEquals(firstHash, secondHash))
		{
			throw new InvalidDataException(
				"The maintenance updater copy SHA-256 is invalid.");
		}
	}

	private static async Task<string> GetSha256Async(
		string filePath,
		CancellationToken cancellationToken)
	{
		await using FileStream stream = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static string QuoteCommandLineArgument(string value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value);

		if (value.Contains('"', StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"A Windows command path cannot contain a quote.",
				nameof(value));
		}

		return $"\"{value}\"";
	}
}
