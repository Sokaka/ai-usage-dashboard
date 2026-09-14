using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record ManagedInstallationUninstallResult(
	bool WasAlreadyAbsent,
	bool InstallRootRemoved,
	string? PreservedInstallRoot,
	string? Warning);

internal sealed class ManagedInstallationUninstaller
{
	private static readonly string[] OwnedDirectoryNames =
	[
		"current",
		"transactions",
		"downloads",
		"updater-cache"
	];
	private readonly ICurrentUserLogonStartupRegistrationStore
		_logonStartupRegistrationStore;
	private readonly IMaintenanceUpdaterCleanup _maintenanceCleanup;
	private readonly IInstalledApplicationRegistrationStore _registrationStore;
	private readonly IManagedPayloadShutdown _shutdown;
	private readonly IManagedStartMenuShortcut _startMenuShortcut;
	private readonly Func<IDisposable> _acquireAppMutex;
	private readonly Func<string> _createExpectedLogonStartupCommand;

	internal ManagedInstallationUninstaller(
		IManagedPayloadShutdown shutdown,
		IInstalledApplicationRegistrationStore registrationStore,
		ICurrentUserLogonStartupRegistrationStore
			logonStartupRegistrationStore,
		IMaintenanceUpdaterCleanup maintenanceCleanup,
		IManagedStartMenuShortcut startMenuShortcut,
		Func<IDisposable>? acquireAppMutex = null,
		Func<string>? createExpectedLogonStartupCommand = null)
	{
		_shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
		_registrationStore = registrationStore ??
			throw new ArgumentNullException(nameof(registrationStore));
		_logonStartupRegistrationStore = logonStartupRegistrationStore ??
			throw new ArgumentNullException(
				nameof(logonStartupRegistrationStore));
		_maintenanceCleanup = maintenanceCleanup ??
			throw new ArgumentNullException(nameof(maintenanceCleanup));
		_startMenuShortcut = startMenuShortcut ??
			throw new ArgumentNullException(nameof(startMenuShortcut));
		_acquireAppMutex = acquireAppMutex ?? (() =>
			AppSingleInstanceMutexLease.Acquire(TimeSpan.FromSeconds(5)));
		_createExpectedLogonStartupCommand =
			createExpectedLogonStartupCommand ??
			WindowsLogonStartupRegistrationContract.CreateExpectedCommand;
	}

	internal async Task<ManagedInstallationUninstallResult> UninstallAsync(
		string runningUpdaterExecutablePath,
		string installRoot,
		string maintenanceRoot,
		string userDataRoot,
		bool shouldManageInstalledAppRegistration,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runningUpdaterExecutablePath);
		ManagedInstallationPaths.EnsureDistinctRoots(
			installRoot,
			maintenanceRoot,
			userDataRoot);
		string normalizedInstallRoot =
			ManagedInstallationPaths.NormalizeDirectory(installRoot);
		string normalizedRunningUpdaterPath = Path.GetFullPath(
			runningUpdaterExecutablePath);

		if (ManagedInstallationPaths.IsPathInsideDirectory(
			normalizedRunningUpdaterPath,
			normalizedInstallRoot))
		{
			throw new InvalidOperationException(
				"Run uninstall from the registered maintenance updater or another " +
				"standalone updater outside the install root.");
		}

		if (!Directory.Exists(normalizedInstallRoot))
		{
			string? absentWarning = null;

			if (shouldManageInstalledAppRegistration)
			{
				absentWarning = CombineWarnings(
					RemoveLogonStartupRegistration(),
					_startMenuShortcut.RemoveIfMatches(normalizedInstallRoot));
				_registrationStore.RemoveIfMatches(normalizedInstallRoot);
			}

			MaintenanceUpdaterCleanupResult absentCleanup =
				shouldManageInstalledAppRegistration
					? _maintenanceCleanup.Cleanup(
						maintenanceRoot,
						normalizedRunningUpdaterPath)
					: new MaintenanceUpdaterCleanupResult(true, null);

			return new ManagedInstallationUninstallResult(
				true,
				true,
				null,
				CombineWarnings(absentWarning, absentCleanup.Warning));
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(
			normalizedInstallRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			normalizedInstallRoot,
			mustExist: true);
		string lockFilePath;
		MaintenanceUpdaterCleanupResult maintenanceCleanup =
			new(true, null);
		string? warning = null;

		using (UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			normalizedInstallRoot))
		{
			lockFilePath = updateLock.LockFilePath;
			ManagedUninstallReceipt receipt =
				await ReadOrCreateOwnershipProofAsync(
					normalizedInstallRoot,
					cancellationToken);
			ValidateOwnedTrees(normalizedInstallRoot);
			string currentDirectory = ManagedInstallationPaths.GetCurrentDirectory(
				normalizedInstallRoot);

			if (Directory.Exists(currentDirectory))
			{
				await _shutdown.EnsureQuiescentAsync(
					currentDirectory,
					receipt.Manifest.PayloadGenerationId,
					cancellationToken);
			}
			else
			{
				_shutdown.EnsureNoPayloadProcesses(currentDirectory);
			}

			using (_acquireAppMutex())
			{
				_shutdown.EnsureNoPayloadProcesses(currentDirectory);
				ValidateOwnedTrees(normalizedInstallRoot);

				if (shouldManageInstalledAppRegistration)
				{
					warning = CombineWarnings(
						RemoveLogonStartupRegistration(),
						_startMenuShortcut.RemoveIfMatches(normalizedInstallRoot));
				}

				DeleteOwnedTrees(normalizedInstallRoot);
			}

			if (shouldManageInstalledAppRegistration)
			{
				_registrationStore.RemoveIfMatches(normalizedInstallRoot);
				maintenanceCleanup = _maintenanceCleanup.Cleanup(
					maintenanceRoot,
					normalizedRunningUpdaterPath);
			}
		}

		DeleteOwnedFileIfPresent(lockFilePath);
		DeleteOwnedFileIfPresent(
			ManagedInstallationPaths.GetUninstallReceipt(normalizedInstallRoot));
		bool installRootRemoved = TryDeleteEmptyInstallRoot(
			normalizedInstallRoot);

		return new ManagedInstallationUninstallResult(
			false,
			installRootRemoved,
			installRootRemoved ? null : normalizedInstallRoot,
			CombineWarnings(warning, maintenanceCleanup.Warning));
	}

	private static string? CombineWarnings(params string?[] warnings)
	{
		string[] presentWarnings = warnings
			.Where(warning => !string.IsNullOrWhiteSpace(warning))
			.Select(warning => warning!)
			.ToArray();
		return presentWarnings.Length == 0
			? null
			: string.Join(" ", presentWarnings);
	}

	private string? RemoveLogonStartupRegistration()
	{
		string expectedCommand;

		try
		{
			expectedCommand = _createExpectedLogonStartupCommand();
		}
		catch (ArgumentException)
		{
			return "Windows 登入啟動命令過長，因此無法自動清理；請至 Windows「啟動應用程式」檢查。";
		}

		LogonStartupRegistrationState state =
			_logonStartupRegistrationStore.RemoveIfMatches(
				expectedCommand);

		return state switch
		{
			LogonStartupRegistrationState.Absent => null,
			LogonStartupRegistrationState.Conflict =>
				"Windows 登入啟動項「AiUsageDashboard」的內容與此安裝不符，因此已保留；請至 Windows「啟動應用程式」檢查。",
			LogonStartupRegistrationState.ExactMatch =>
				throw new InvalidOperationException(
					"Windows logon startup registration remained after removal."),
			_ => throw new InvalidOperationException(
				"Windows logon startup registration state is unsupported.")
		};
	}

	private async Task<ManagedUninstallReceipt>
		ReadOrCreateOwnershipProofAsync(
			string installRoot,
			CancellationToken cancellationToken)
	{
		string currentDirectory = ManagedInstallationPaths.GetCurrentDirectory(
			installRoot);
		string receiptPath = ManagedInstallationPaths.GetUninstallReceipt(
			installRoot);

		if (!Directory.Exists(currentDirectory))
		{
			if (!File.Exists(receiptPath))
			{
				throw new InvalidDataException(
					"The install root no longer contains a verifiable managed " +
					"installation or resumable uninstall receipt.");
			}

			return await ManagedUninstallReceipt.ReadAsync(
				receiptPath,
				installRoot,
				cancellationToken);
		}

		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			currentDirectory,
			mustExist: true);
		string manifestPath = ManagedInstallationPaths.GetInstalledManifest(
			installRoot);
		string appExecutablePath =
			ManagedInstallationPaths.GetInstalledAppExecutable(installRoot);
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			manifestPath,
			mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(
			appExecutablePath,
			mustExist: true);
		UpdateManifest manifest = await UpdateManifest.ReadAsync(
			manifestPath,
			cancellationToken);

		if (!File.Exists(receiptPath))
		{
			return await ManagedUninstallReceipt.WriteNewAsync(
				receiptPath,
				installRoot,
				manifest,
				cancellationToken);
		}

		ManagedUninstallReceipt existingReceipt =
			await ManagedUninstallReceipt.ReadAsync(
				receiptPath,
				installRoot,
				cancellationToken);

		if (!string.Equals(
				existingReceipt.Manifest.Version,
				manifest.Version,
				StringComparison.Ordinal) ||
			!string.Equals(
				existingReceipt.Manifest.PayloadGenerationId,
				manifest.PayloadGenerationId,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"The uninstall receipt does not match the installed payload.");
		}

		return existingReceipt;
	}

	private static void ValidateOwnedTrees(string installRoot)
	{
		foreach (string directoryName in OwnedDirectoryNames)
		{
			string directoryPath = Path.Combine(installRoot, directoryName);
			ValidateOwnedTree(directoryPath);
		}
	}

	private static void ValidateOwnedTree(string directoryPath)
	{
		if (File.Exists(directoryPath))
		{
			throw new IOException(
				"An updater-owned directory path is occupied by a file.");
		}

		if (!Directory.Exists(directoryPath))
		{
			return;
		}

		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			directoryPath,
			mustExist: true);

		foreach (FileSystemInfo entry in new DirectoryInfo(directoryPath)
			.GetFileSystemInfos())
		{
			entry.Refresh();

			if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new InvalidDataException(
					"Updater-owned uninstall trees cannot contain reparse points.");
			}

			if (entry is DirectoryInfo childDirectory)
			{
				ValidateOwnedTree(childDirectory.FullName);
			}
		}
	}

	private static void DeleteOwnedTrees(string installRoot)
	{
		foreach (string directoryName in OwnedDirectoryNames)
		{
			string directoryPath = Path.Combine(installRoot, directoryName);

			if (Directory.Exists(directoryPath))
			{
				DeleteOwnedTree(directoryPath);
			}
		}
	}

	private static void DeleteOwnedTree(string directoryPath)
	{
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			directoryPath,
			mustExist: true);

		foreach (FileSystemInfo entry in new DirectoryInfo(directoryPath)
			.GetFileSystemInfos())
		{
			entry.Refresh();

			if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new InvalidDataException(
					"Updater-owned uninstall trees cannot contain reparse points.");
			}

			if (entry is DirectoryInfo childDirectory)
			{
				DeleteOwnedTree(childDirectory.FullName);
				continue;
			}

			ClearReadOnlyAttribute(entry.FullName, entry.Attributes);
			File.Delete(entry.FullName);
		}

		Directory.Delete(directoryPath, recursive: false);
	}

	private static void DeleteOwnedFileIfPresent(string filePath)
	{
		if (!File.Exists(filePath))
		{
			return;
		}

		UpdateTransaction.ThrowIfNotOrdinaryFile(filePath, mustExist: true);
		FileAttributes attributes = File.GetAttributes(filePath);
		ClearReadOnlyAttribute(filePath, attributes);
		File.Delete(filePath);
	}

	private static bool TryDeleteEmptyInstallRoot(string installRoot)
	{
		if (!Directory.Exists(installRoot))
		{
			return true;
		}

		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			installRoot,
			mustExist: true);

		try
		{
			Directory.Delete(installRoot, recursive: false);
			return true;
		}
		catch (IOException) when (Directory.Exists(installRoot))
		{
			return false;
		}
	}

	private static void ClearReadOnlyAttribute(
		string filePath,
		FileAttributes attributes)
	{
		if ((attributes & FileAttributes.ReadOnly) != 0)
		{
			File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
		}
	}
}
