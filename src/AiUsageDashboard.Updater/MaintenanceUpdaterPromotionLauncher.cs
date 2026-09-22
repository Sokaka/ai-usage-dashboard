using System.Diagnostics;
using System.Globalization;

namespace AiUsageDashboard.Updater;

internal interface IMaintenanceUpdaterPromotionLauncher
{
	Task<bool> LaunchAsync(
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken = default);
}

internal sealed class MaintenanceUpdaterPromotionLauncher :
	IMaintenanceUpdaterPromotionLauncher
{
	public async Task<bool> LaunchAsync(
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken = default)
	{
		return await LaunchAsync(
			options,
			expectedReceipt: null,
			cancellationToken);
	}

	internal async Task<bool> LaunchRetryAsync(
		MaintenanceUpdaterPromotionOptions options,
		MaintenanceUpdaterPromotionReceipt expectedReceipt,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(expectedReceipt);
		return await LaunchAsync(
			options,
			expectedReceipt,
			cancellationToken);
	}

	private static async Task<bool> LaunchAsync(
		MaintenanceUpdaterPromotionOptions options,
		MaintenanceUpdaterPromotionReceipt? expectedReceipt,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		string sourcePath = ManagedInstallationPaths
			.GetMaintenanceUpdaterGeneration(
				options.MaintenanceRoot,
				options.SourceSha256);
		ProcessStartInfo startInfo = CreateStartInfo(sourcePath, options);
		MaintenanceUpdaterPromotionReceiptStore receiptStore = new(
			options.MaintenanceRoot);
		InvalidOperationException? launchException = null;

		using (UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			options.InstallRoot))
		{
			MaintenanceUpdaterFile.EnsureCompatible(sourcePath);
			string observedHash = await MaintenanceUpdaterFile.GetSha256Async(
				sourcePath,
				cancellationToken);

			if (!string.Equals(
					observedHash,
					options.SourceSha256,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					$"Maintenance updater generation '{sourcePath}' changed before " +
					"promotion launch.");
			}

			string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
				options.MaintenanceRoot);
			MaintenanceUpdaterFile.EnsureCompatible(canonicalPath);
			string canonicalHash = await MaintenanceUpdaterFile.GetSha256Async(
				canonicalPath,
				cancellationToken);

			if (string.Equals(
					canonicalHash,
					options.SourceSha256,
					StringComparison.Ordinal))
			{
				return false;
			}

			if (!string.Equals(
					canonicalHash,
					options.ExpectedCanonicalSha256,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					$"Canonical maintenance updater '{canonicalPath}' changed " +
					"before promotion launch.");
			}

			bool receiptWasSaved = expectedReceipt is null
				? await SavePendingAsync(
					receiptStore,
					options,
					cancellationToken)
				: await receiptStore
					.SavePendingIfSnapshotMatchesWhileInstallLockHeldAsync(
						expectedReceipt,
						options,
						cancellationToken);

			if (!receiptWasSaved)
			{
				return false;
			}

			try
			{
				using Process process = Process.Start(startInfo) ??
					throw new InvalidOperationException(
						"Windows did not start maintenance updater promotion.");
				// promotion 必須跨過目前 updater 的結束點，不能在此等待子程序。
				return true;
			}
			catch (Exception exception) when (
				exception is InvalidOperationException or
					System.ComponentModel.Win32Exception)
			{
				launchException = new InvalidOperationException(
					$"Unable to launch maintenance updater promotion from " +
					$"'{sourcePath}'.",
					exception);
			}
		}

		InvalidOperationException observedLaunchException = launchException ??
			throw new InvalidOperationException(
				"Maintenance updater promotion launch failed without an error.");

		try
		{
			await receiptStore.SaveFailureIfCurrentAsync(
				options,
				observedLaunchException,
				CancellationToken.None);
		}
		catch (Exception receiptException)
		{
			throw new AggregateException(
				"Maintenance updater promotion launch failed and its " +
				"diagnostic receipt could not be saved.",
				observedLaunchException,
				receiptException);
		}

		throw observedLaunchException;
	}

	private static async Task<bool> SavePendingAsync(
		MaintenanceUpdaterPromotionReceiptStore receiptStore,
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken)
	{
		await receiptStore.SavePendingWhileInstallLockHeldAsync(
			options,
			cancellationToken);
		return true;
	}

	internal static ProcessStartInfo CreateStartInfo(
		string sourcePath,
		MaintenanceUpdaterPromotionOptions options)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
		ArgumentNullException.ThrowIfNull(options);
		ProcessStartInfo startInfo = new()
		{
			FileName = Path.GetFullPath(sourcePath),
			WorkingDirectory = ManagedInstallationPaths.NormalizeDirectory(
				options.MaintenanceRoot),
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(MaintenanceUpdaterPromotionCommandLine.Command);
		startInfo.ArgumentList.Add("--source-sha256");
		startInfo.ArgumentList.Add(options.SourceSha256);
		startInfo.ArgumentList.Add("--expected-canonical-sha256");
		startInfo.ArgumentList.Add(options.ExpectedCanonicalSha256);
		startInfo.ArgumentList.Add("--parent-process-id");
		startInfo.ArgumentList.Add(options.ParentIdentity.ProcessId.ToString(
			CultureInfo.InvariantCulture));
		startInfo.ArgumentList.Add("--parent-process-start-time-utc-ticks");
		startInfo.ArgumentList.Add(
			options.ParentIdentity.ProcessStartTimeUtcTicks.ToString(
				CultureInfo.InvariantCulture));
		return startInfo;
	}
}
