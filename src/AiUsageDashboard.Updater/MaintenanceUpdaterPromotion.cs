using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class MaintenanceUpdaterPromotion
{
	private static readonly TimeSpan ParentExitTimeout = Timeout.InfiniteTimeSpan;
	private readonly IExactProcessExitWaiter _processExitWaiter;

	internal MaintenanceUpdaterPromotion(
		IExactProcessExitWaiter processExitWaiter)
	{
		_processExitWaiter = processExitWaiter ??
			throw new ArgumentNullException(nameof(processExitWaiter));
	}

	internal async Task PromoteAndRecordAsync(
		MaintenanceUpdaterPromotionOptions options,
		string runningExecutablePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		MaintenanceUpdaterPromotionReceiptStore receiptStore = new(
			options.MaintenanceRoot);

		try
		{
			await PromoteAsync(
				options,
				runningExecutablePath,
				cancellationToken);
			await receiptStore.DeleteIfMatchesAsync(
				options,
				CancellationToken.None);
		}
		catch (Exception promotionException)
		{
			try
			{
				await receiptStore.SaveFailureIfCurrentAsync(
					options,
					promotionException,
					CancellationToken.None);
			}
			catch (Exception receiptException)
			{
				throw new AggregateException(
					"Maintenance updater promotion failed and its diagnostic " +
					"receipt could not be saved.",
					promotionException,
					receiptException);
			}

			throw;
		}
	}

	internal async Task PromoteAsync(
		MaintenanceUpdaterPromotionOptions options,
		string runningExecutablePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentException.ThrowIfNullOrWhiteSpace(runningExecutablePath);
		string installRoot = ManagedInstallationPaths.NormalizeDirectory(
			options.InstallRoot);
		string maintenanceRoot = ManagedInstallationPaths.NormalizeDirectory(
			options.MaintenanceRoot);
		if (string.Equals(
				installRoot,
				maintenanceRoot,
				StringComparison.OrdinalIgnoreCase) ||
			ManagedInstallationPaths.IsPathInsideDirectory(
				installRoot,
				maintenanceRoot) ||
			ManagedInstallationPaths.IsPathInsideDirectory(
				maintenanceRoot,
				installRoot))
		{
			throw new InvalidOperationException(
				"The canonical install and maintenance roots must not overlap.");
		}
		string sourcePath = ManagedInstallationPaths
			.GetMaintenanceUpdaterGeneration(
				maintenanceRoot,
				options.SourceSha256);
		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			maintenanceRoot);
		string resolvedRunningPath = Path.GetFullPath(runningExecutablePath);

		if (!string.Equals(
				resolvedRunningPath,
				sourcePath,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				$"Maintenance updater promotion must run from the verified " +
				$"generation path '{sourcePath}'.");
		}

		await _processExitWaiter.WaitForExitAsync(
			options.ParentIdentity,
			ParentExitTimeout,
			cancellationToken);
		using UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			installRoot);
		MaintenanceUpdaterPromotionReceiptStore receiptStore = new(
			maintenanceRoot);
		if (!await receiptStore.IsCurrentWhileInstallLockHeldAsync(
				options,
				cancellationToken))
		{
			return;
		}

		await ValidateSourceAndCanonicalAsync(
			maintenanceRoot,
			sourcePath,
			canonicalPath,
			options.SourceSha256,
			options.ExpectedCanonicalSha256,
			cancellationToken);
		string currentCanonicalHash = await MaintenanceUpdaterFile.GetSha256Async(
			canonicalPath,
			cancellationToken);

		if (string.Equals(
				currentCanonicalHash,
				options.SourceSha256,
				StringComparison.Ordinal))
		{
			return;
		}

		string temporaryPath = canonicalPath + "." +
			Guid.NewGuid().ToString("N") + ".promoting";

		try
		{
			UpdateTransaction.ThrowIfNotOrdinaryFile(
				temporaryPath,
				mustExist: false);
			File.Copy(sourcePath, temporaryPath, overwrite: false);
			await MaintenanceUpdaterFile.EnsureFilesMatchAsync(
				sourcePath,
				temporaryPath,
				cancellationToken);

			try
			{
				File.Move(temporaryPath, canonicalPath, overwrite: true);
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				throw new IOException(
					$"Unable to atomically promote maintenance updater " +
					$"'{sourcePath}' to '{canonicalPath}'.",
					exception);
			}

			MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
			string promotedHash = await MaintenanceUpdaterFile.GetSha256Async(
				canonicalPath,
				cancellationToken);

			if (!string.Equals(
					promotedHash,
					options.SourceSha256,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					$"Promoted maintenance updater '{canonicalPath}' has an " +
					"unexpected SHA-256.");
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
	}

	private static async Task ValidateSourceAndCanonicalAsync(
		string maintenanceRoot,
		string sourcePath,
		string canonicalPath,
		string sourceSha256,
		string expectedCanonicalSha256,
		CancellationToken cancellationToken)
	{
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(maintenanceRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			maintenanceRoot,
			mustExist: true);
		MaintenanceUpdaterFile.EnsureCompatible(sourcePath);
		MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
		string observedSourceHash = await MaintenanceUpdaterFile.GetSha256Async(
			sourcePath,
			cancellationToken);

		if (!string.Equals(
				observedSourceHash,
				sourceSha256,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				$"Maintenance updater generation '{sourcePath}' does not match " +
				"its content-addressed SHA-256.");
		}

		string observedCanonicalHash = await MaintenanceUpdaterFile.GetSha256Async(
			canonicalPath,
			cancellationToken);

		if (!string.Equals(
				observedCanonicalHash,
				expectedCanonicalSha256,
				StringComparison.Ordinal) &&
			!string.Equals(
				observedCanonicalHash,
				sourceSha256,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				$"Canonical maintenance updater '{canonicalPath}' changed before " +
				"the verified generation could be promoted.");
		}
	}
}
