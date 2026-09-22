using System.Diagnostics;
using System.Security.Cryptography;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record MaintenanceUpdaterCleanupResult(
	bool IsCompleteOrScheduled,
	string? Warning);

internal interface IMaintenanceUpdaterCleanup
{
	MaintenanceUpdaterCleanupResult Cleanup(
		string maintenanceRoot,
		string runningUpdaterExecutablePath);
}

internal sealed class MaintenanceUpdaterCleanup : IMaintenanceUpdaterCleanup
{
	private sealed record OwnedUpdaterFile(string FullPath, string Sha256);

	private const int MaximumOwnedUpdaterFiles = 8;
	private const string CleanupCommand =
		"for /L %G in (1,0,2) do @(" +
		"del /F /Q \"%AIUD_MAINTENANCE_EXECUTABLE%\" >nul 2>nul & " +
		"if not exist \"%AIUD_MAINTENANCE_EXECUTABLE%\" (" +
		"cd /D \"%AIUD_SYSTEM_DIRECTORY%\" & " +
		"rmdir \"%AIUD_MAINTENANCE_ROOT%\" 2>nul & " +
		"exit /B 0" +
		") & " +
		"\"%AIUD_SYSTEM_PING%\" -n 2 127.0.0.1 >nul" +
		")";
	private readonly Action<string> _setCurrentDirectory;
	private readonly Func<ProcessStartInfo, Process?> _startProcess;

	internal MaintenanceUpdaterCleanup(
		Func<ProcessStartInfo, Process?>? startProcess = null,
		Action<string>? setCurrentDirectory = null)
	{
		_startProcess = startProcess ?? Process.Start;
		_setCurrentDirectory = setCurrentDirectory ??
			(path => Environment.CurrentDirectory = path);
	}

	public MaintenanceUpdaterCleanupResult Cleanup(
		string maintenanceRoot,
		string runningUpdaterExecutablePath)
	{
		string normalizedMaintenanceRoot =
			ManagedInstallationPaths.NormalizeDirectory(maintenanceRoot);

		if (!Directory.Exists(normalizedMaintenanceRoot))
		{
			return new MaintenanceUpdaterCleanupResult(true, null);
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(
			normalizedMaintenanceRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			normalizedMaintenanceRoot,
			mustExist: true);
		if (!TryValidateOwnedMaintenanceFiles(normalizedMaintenanceRoot))
		{
			return CreatePreservationWarning(normalizedMaintenanceRoot);
		}
		if (!TryDeleteOwnedPromotionReceiptFiles(normalizedMaintenanceRoot))
		{
			return CreatePreservationWarning(normalizedMaintenanceRoot);
		}
		if (!TryDeleteOwnedPromotionTemporaryFiles(normalizedMaintenanceRoot))
		{
			return CreatePreservationWarning(normalizedMaintenanceRoot);
		}

		if (!TryReadOwnedUpdaterFiles(
				normalizedMaintenanceRoot,
				out List<OwnedUpdaterFile> ownedFiles))
		{
			return CreatePreservationWarning(normalizedMaintenanceRoot);
		}

		if (ownedFiles.Count == 0)
		{
			Directory.Delete(normalizedMaintenanceRoot, recursive: false);
			return new MaintenanceUpdaterCleanupResult(true, null);
		}

		string normalizedRunningPath = Path.GetFullPath(
			runningUpdaterExecutablePath);

		if (ManagedInstallationPaths.IsPathInsideDirectory(
			normalizedRunningPath,
			normalizedMaintenanceRoot))
		{
			OwnedUpdaterFile? runningUpdater = ownedFiles.SingleOrDefault(file =>
				string.Equals(
					file.FullPath,
					normalizedRunningPath,
					StringComparison.OrdinalIgnoreCase));

			if (runningUpdater is null)
			{
				throw new InvalidOperationException(
					"The running updater is at an unexpected maintenance path.");
			}

			foreach (OwnedUpdaterFile file in ownedFiles.Where(file =>
				!string.Equals(
					file.FullPath,
					normalizedRunningPath,
					StringComparison.OrdinalIgnoreCase)))
			{
				MaintenanceUpdaterCleanupResult? deletionFailure =
					TryDeleteOwnedUpdaterFile(file);

				if (deletionFailure is not null)
				{
					return deletionFailure;
				}
			}

			ValidateOwnedUpdaterFile(runningUpdater);
			string removalUpdaterPath =
				ManagedInstallationPaths.GetMaintenanceUpdaterRemovalGeneration(
					normalizedMaintenanceRoot,
					runningUpdater.Sha256,
					Guid.NewGuid().ToString("N"));
			UpdateTransaction.ThrowIfNotOrdinaryFile(
				removalUpdaterPath,
				mustExist: false);
			ClearReadOnlyAttribute(runningUpdater.FullPath);
			// Parent CWD handle 會阻止 Windows 移除清空後的 maintenance root。
			_setCurrentDirectory(Environment.SystemDirectory);
			File.Move(
				runningUpdater.FullPath,
				removalUpdaterPath,
				overwrite: false);

			try
			{
				ProcessStartInfo startInfo = CreateSelfCleanupStartInfo(
					normalizedMaintenanceRoot,
					removalUpdaterPath);
				using Process cleanupProcess = _startProcess(startInfo) ??
					throw new InvalidOperationException(
						"Windows did not start the maintenance cleanup process.");
			}
			catch (Exception startException)
			{
				try
				{
					File.Move(
						removalUpdaterPath,
						runningUpdater.FullPath,
						overwrite: false);
				}
				catch (Exception restoreException)
				{
					throw new AggregateException(
						"Maintenance cleanup did not start and its updater " +
							"could not be restored.",
						startException,
						restoreException);
				}

				throw;
			}

			return new MaintenanceUpdaterCleanupResult(true, null);
		}

		foreach (OwnedUpdaterFile file in ownedFiles)
		{
			MaintenanceUpdaterCleanupResult? deletionFailure =
				TryDeleteOwnedUpdaterFile(file);

			if (deletionFailure is not null)
			{
				return deletionFailure;
			}
		}

		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			normalizedMaintenanceRoot,
			mustExist: true);
		Directory.Delete(normalizedMaintenanceRoot, recursive: false);
		return new MaintenanceUpdaterCleanupResult(true, null);
	}

	internal static ProcessStartInfo CreateSelfCleanupStartInfo(
		string maintenanceRoot,
		string maintenanceExecutablePath)
	{
		string normalizedMaintenanceRoot =
			ManagedInstallationPaths.NormalizeDirectory(maintenanceRoot);
		string normalizedExecutablePath = Path.GetFullPath(
			maintenanceExecutablePath);

		if (!TryReadOwnedUpdaterFiles(
				normalizedMaintenanceRoot,
				out List<OwnedUpdaterFile> ownedFiles) ||
			(ownedFiles.Count != 1) ||
			!string.Equals(
				ownedFiles[0].FullPath,
				normalizedExecutablePath,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"The maintenance updater directory contains unexpected files.");
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
			// cmd /c 以 raw Arguments 才能保留整組命令；路徑只透過環境變數傳入。
			Arguments = "/d /e:on /v:off /q /c " + CleanupCommand,
			WorkingDirectory = Environment.SystemDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		startInfo.Environment["AIUD_MAINTENANCE_ROOT"] =
			normalizedMaintenanceRoot;
		startInfo.Environment["AIUD_MAINTENANCE_EXECUTABLE"] =
			normalizedExecutablePath;
		startInfo.Environment["AIUD_SYSTEM_DIRECTORY"] =
			Environment.SystemDirectory;
		startInfo.Environment["AIUD_SYSTEM_PING"] =
			Path.Combine(Environment.SystemDirectory, "ping.exe");
		startInfo.Environment["PATH"] = Environment.SystemDirectory;
		return startInfo;
	}

	private static MaintenanceUpdaterCleanupResult CreatePreservationWarning(
		string maintenanceRoot)
	{
		return new MaintenanceUpdaterCleanupResult(
			false,
			$"Maintenance updater cleanup preserved unexpected files at " +
				$"'{maintenanceRoot}'.");
	}

	private static MaintenanceUpdaterCleanupResult CreateRetryWarning(
		string updaterPath,
		Exception exception)
	{
		return new MaintenanceUpdaterCleanupResult(
			false,
			$"Maintenance updater cleanup canceled any pending promotion but " +
				$"could not remove updater '{updaterPath}'. Close all updater " +
				$"processes and run uninstall again. Windows reported: " +
				exception.Message);
	}

	private static string GetSha256(string filePath)
	{
		using FileStream stream = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	}

	private static bool TryReadOwnedUpdaterFiles(
		string maintenanceRoot,
		out List<OwnedUpdaterFile> ownedFiles)
	{
		return TryReadOwnedUpdaterFiles(
			new DirectoryInfo(maintenanceRoot).GetFileSystemInfos(),
			out ownedFiles);
	}

	private static bool TryReadOwnedUpdaterFiles(
		IReadOnlyCollection<FileSystemInfo> entries,
		out List<OwnedUpdaterFile> ownedFiles)
	{
		ownedFiles = [];

		if (entries.Count > MaximumOwnedUpdaterFiles)
		{
			return false;
		}

		foreach (FileSystemInfo entry in entries)
		{
			entry.Refresh();

			if ((entry is DirectoryInfo) ||
				((entry.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			bool isStableName = string.Equals(
				entry.Name,
				ManagedInstallationPaths.MaintenanceUpdaterFileName,
				StringComparison.Ordinal);
			bool isGenerationName =
				ManagedInstallationPaths.TryGetMaintenanceUpdaterGenerationHash(
					entry.Name,
					out string generationHash);

			if (!isStableName && !isGenerationName)
			{
				return false;
			}

			UpdateTransaction.ThrowIfNotOrdinaryFile(
				entry.FullName,
				mustExist: true);
			FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(
				entry.FullName);

			if (!string.Equals(
					versionInfo.ProductName,
					"AiUsageDashboard.Updater",
					StringComparison.Ordinal) ||
				string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
			{
				return false;
			}

			string sha256 = GetSha256(entry.FullName);

			if (isGenerationName &&
				!string.Equals(
					sha256,
					generationHash,
					StringComparison.Ordinal))
			{
				return false;
			}

			ownedFiles.Add(new OwnedUpdaterFile(entry.FullName, sha256));
		}

		return true;
	}

	private static bool TryValidateOwnedMaintenanceFiles(
		string maintenanceRoot)
	{
		List<FileSystemInfo> updaterEntries = [];

		foreach (FileSystemInfo entry in new DirectoryInfo(maintenanceRoot)
			.GetFileSystemInfos())
		{
			bool isPromotionReceipt =
				MaintenanceUpdaterPromotionReceiptStore.IsOwnedFileName(
					entry.Name);
			bool isPromotionTemporary =
				IsOwnedPromotionTemporaryFileName(entry.Name);

			if (!isPromotionReceipt && !isPromotionTemporary)
			{
				updaterEntries.Add(entry);
				continue;
			}

			entry.Refresh();
			if ((entry is DirectoryInfo) ||
				((entry.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			UpdateTransaction.ThrowIfNotOrdinaryFile(
				entry.FullName,
				mustExist: true);
		}

		return TryReadOwnedUpdaterFiles(updaterEntries, out _);
	}

	private static bool TryDeleteOwnedPromotionReceiptFiles(
		string maintenanceRoot)
	{
		foreach (FileSystemInfo entry in new DirectoryInfo(maintenanceRoot)
			.GetFileSystemInfos())
		{
			if (!MaintenanceUpdaterPromotionReceiptStore.IsOwnedFileName(
					entry.Name))
			{
				continue;
			}

			entry.Refresh();
			if ((entry is DirectoryInfo) ||
				((entry.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			UpdateTransaction.ThrowIfNotOrdinaryFile(
				entry.FullName,
				mustExist: true);
			ClearReadOnlyAttribute(entry.FullName);
			File.Delete(entry.FullName);
		}

		return true;
	}

	private static bool TryDeleteOwnedPromotionTemporaryFiles(
		string maintenanceRoot)
	{
		foreach (FileSystemInfo entry in new DirectoryInfo(maintenanceRoot)
			.GetFileSystemInfos())
		{
			if (!IsOwnedPromotionTemporaryFileName(entry.Name))
			{
				continue;
			}

			entry.Refresh();
			if ((entry is DirectoryInfo) ||
				((entry.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			UpdateTransaction.ThrowIfNotOrdinaryFile(
				entry.FullName,
				mustExist: true);
			ClearReadOnlyAttribute(entry.FullName);
			File.Delete(entry.FullName);
		}

		return true;
	}

	private static bool IsOwnedPromotionTemporaryFileName(string fileName)
	{
		const string temporarySuffix = ".promoting";
		string temporaryPrefix =
			ManagedInstallationPaths.MaintenanceUpdaterFileName + ".";
		if (!fileName.StartsWith(temporaryPrefix, StringComparison.Ordinal) ||
			!fileName.EndsWith(temporarySuffix, StringComparison.Ordinal))
		{
			return false;
		}

		string generationId = fileName.Substring(
			temporaryPrefix.Length,
			fileName.Length - temporaryPrefix.Length - temporarySuffix.Length);
		return (generationId.Length == 32) &&
			generationId.All(character =>
				((character >= '0') && (character <= '9')) ||
				((character >= 'a') && (character <= 'f')));
	}

	private static void ClearReadOnlyAttribute(string filePath)
	{
		FileAttributes attributes = File.GetAttributes(filePath);

		if ((attributes & FileAttributes.ReadOnly) != 0)
		{
			File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
		}
	}

	private static void ValidateOwnedUpdaterFile(OwnedUpdaterFile file)
	{
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			file.FullPath,
			mustExist: true);

		if (!string.Equals(
				GetSha256(file.FullPath),
				file.Sha256,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"A maintenance updater changed before cleanup.");
		}
	}

	private static MaintenanceUpdaterCleanupResult?
		TryDeleteOwnedUpdaterFile(OwnedUpdaterFile file)
	{
		try
		{
			ValidateOwnedUpdaterFile(file);
			ClearReadOnlyAttribute(file.FullPath);
			File.Delete(file.FullPath);
			return null;
		}
		catch (Exception exception) when (
			((exception is IOException) &&
				(exception is not InvalidDataException)) ||
			(exception is UnauthorizedAccessException))
		{
			return CreateRetryWarning(file.FullPath, exception);
		}
	}
}
