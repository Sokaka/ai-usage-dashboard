using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record DelegatedMaintenanceUpdaterPromotionContext(
	ExactProcessIdentity ParentIdentity,
	string ExpectedCanonicalSha256);

internal enum DelegatedMaintenanceUpdaterPromotionState
{
	CanonicalCurrent,
	PendingForExactParent
}

internal sealed class DelegatedMaintenanceUpdaterPromotionCoordinator
{
	private readonly IMaintenanceUpdaterPromotionLauncher _promotionLauncher;
	private readonly IProcessLineageProbe _processLineageProbe;

	internal DelegatedMaintenanceUpdaterPromotionCoordinator(
		IProcessLineageProbe processLineageProbe,
		IMaintenanceUpdaterPromotionLauncher promotionLauncher)
	{
		_processLineageProbe = processLineageProbe ??
			throw new ArgumentNullException(nameof(processLineageProbe));
		_promotionLauncher = promotionLauncher ??
			throw new ArgumentNullException(nameof(promotionLauncher));
	}

	internal async Task<DelegatedMaintenanceUpdaterPromotionContext?>
		CaptureAsync(
			CurrentUpdaterIdentity currentUpdater,
			UpdaterCommandLineOptions options,
			CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(currentUpdater);
		ArgumentNullException.ThrowIfNull(options);

		if (options.ShouldRefreshUpdater || !options.ShouldRegisterInstalledApp)
		{
			return null;
		}

		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			options.MaintenanceRoot);

		if (string.Equals(
				currentUpdater.ExecutablePath,
				canonicalPath,
				StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		ProcessLineage lineage = _processLineageProbe.CaptureCurrent();

		if (!string.Equals(
				lineage.CurrentExecutablePath,
				currentUpdater.ExecutablePath,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				$"The running updater path '{lineage.CurrentExecutablePath}' does not " +
				$"match the inspected executable '{currentUpdater.ExecutablePath}'.");
		}

		if (!string.Equals(
				lineage.ParentExecutablePath,
				canonicalPath,
				StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
		string canonicalHash = await MaintenanceUpdaterFile.GetSha256Async(
			canonicalPath,
			cancellationToken);

		if (string.Equals(
				canonicalHash,
				currentUpdater.Sha256,
				StringComparison.Ordinal))
		{
			return null;
		}

		return new DelegatedMaintenanceUpdaterPromotionContext(
			lineage.ParentIdentity,
			canonicalHash);
	}

	internal async Task<DelegatedMaintenanceUpdaterPromotionState> ScheduleAsync(
		DelegatedMaintenanceUpdaterPromotionContext context,
		CurrentUpdaterIdentity currentUpdater,
		UpdateReleaseArtifact availableUpdater,
		UpdaterCommandLineOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(currentUpdater);
		ArgumentNullException.ThrowIfNull(availableUpdater);
		ArgumentNullException.ThrowIfNull(options);

		if (options.ShouldRefreshUpdater || !options.ShouldRegisterInstalledApp)
		{
			throw new InvalidOperationException(
				"Maintenance updater promotion requires a delegated canonical update.");
		}

		EnsureSignedCacheIdentity(currentUpdater, availableUpdater, options);

		string generationPath = ManagedInstallationPaths
			.GetMaintenanceUpdaterGeneration(
				options.MaintenanceRoot,
				availableUpdater.Sha256);
		MaintenanceUpdaterFile.EnsureCompatible(generationPath);
		string generationHash = await MaintenanceUpdaterFile.GetSha256Async(
			generationPath,
			cancellationToken);

		if (!string.Equals(
				generationHash,
				availableUpdater.Sha256,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				$"Maintenance updater generation '{generationPath}' does not match " +
				"the signed updater artifact.");
		}

		MaintenanceUpdaterPromotionOptions promotionOptions =
			CreatePromotionOptions(context, availableUpdater, options);
		_ = await _promotionLauncher.LaunchAsync(
			promotionOptions,
			cancellationToken);
		return await VerifyScheduledOrPromotedAsync(
			promotionOptions,
			cancellationToken);
	}

	internal async Task<bool> IsCanonicalUpdaterCurrentAsync(
		CurrentUpdaterIdentity currentUpdater,
		UpdateReleaseArtifact availableUpdater,
		UpdaterCommandLineOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(currentUpdater);
		ArgumentNullException.ThrowIfNull(availableUpdater);
		ArgumentNullException.ThrowIfNull(options);

		if (options.ShouldRefreshUpdater || !options.ShouldRegisterInstalledApp)
		{
			return true;
		}

		EnsureSignedCacheIdentity(currentUpdater, availableUpdater, options);
		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			options.MaintenanceRoot);
		MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
		string canonicalHash = await MaintenanceUpdaterFile.GetSha256Async(
			canonicalPath,
			cancellationToken);
		return string.Equals(
			canonicalHash,
			availableUpdater.Sha256,
			StringComparison.Ordinal);
	}

	private static void EnsureSignedCacheIdentity(
		CurrentUpdaterIdentity currentUpdater,
		UpdateReleaseArtifact availableUpdater,
		UpdaterCommandLineOptions options)
	{
		UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(
			currentUpdater,
			availableUpdater);
		string expectedCachePath = UpdaterArtifactCache.GetExecutablePath(
			availableUpdater,
			options.InstallRoot);

		if (!string.Equals(
				currentUpdater.ExecutablePath,
				expectedCachePath,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				$"Delegated updater '{currentUpdater.ExecutablePath}' is not running " +
				$"from the signed cache path '{expectedCachePath}'.");
		}
	}

	private static MaintenanceUpdaterPromotionOptions CreatePromotionOptions(
		DelegatedMaintenanceUpdaterPromotionContext context,
		UpdateReleaseArtifact availableUpdater,
		UpdaterCommandLineOptions options)
	{
		return new MaintenanceUpdaterPromotionOptions(
			options.InstallRoot,
			options.MaintenanceRoot,
			availableUpdater.Sha256,
			context.ExpectedCanonicalSha256,
			context.ParentIdentity);
	}

	private static async Task<DelegatedMaintenanceUpdaterPromotionState>
		VerifyScheduledOrPromotedAsync(
			MaintenanceUpdaterPromotionOptions promotionOptions,
			CancellationToken cancellationToken)
	{
		using UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			promotionOptions.InstallRoot);
		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			promotionOptions.MaintenanceRoot);
		MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
		string canonicalHash = await MaintenanceUpdaterFile.GetSha256Async(
			canonicalPath,
			cancellationToken);

		if (string.Equals(
				canonicalHash,
				promotionOptions.SourceSha256,
				StringComparison.Ordinal))
		{
			return DelegatedMaintenanceUpdaterPromotionState.CanonicalCurrent;
		}

		if (!string.Equals(
				canonicalHash,
				promotionOptions.ExpectedCanonicalSha256,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				$"Canonical maintenance updater '{canonicalPath}' changed after " +
					"promotion scheduling.");
		}

		MaintenanceUpdaterPromotionReceiptStore receiptStore = new(
			promotionOptions.MaintenanceRoot);
		MaintenanceUpdaterPromotionReceipt? receipt = await receiptStore.LoadAsync(
			cancellationToken);

		if (receipt is null)
		{
			InvalidOperationException exception = new(
				"Maintenance updater promotion returned without a durable pending " +
					$"receipt for parent process {promotionOptions.ParentIdentity.ProcessId}.");
			await receiptStore.SaveFailureWhileInstallLockHeldAsync(
				promotionOptions,
				exception,
				CancellationToken.None);
			throw exception;
		}

		if (!MaintenanceUpdaterPromotionReceiptStore.IsPendingFor(
				receipt,
				promotionOptions))
		{
			string failureDetail = receipt.Status ==
				MaintenanceUpdaterPromotionReceiptStore.FailedStatus
					? $" The recorded failure was {receipt.FailureType}: " +
						receipt.FailureMessage
					: string.Empty;
			throw new InvalidDataException(
				"Maintenance updater promotion did not preserve the exact pending " +
					$"receipt for parent process {promotionOptions.ParentIdentity.ProcessId}." +
					failureDetail);
		}

		return DelegatedMaintenanceUpdaterPromotionState.PendingForExactParent;
	}
}
