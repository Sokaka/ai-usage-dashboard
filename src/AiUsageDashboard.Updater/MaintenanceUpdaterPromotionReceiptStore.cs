using System.Text.Json;
using System.Text.Json.Serialization;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed record MaintenanceUpdaterPromotionReceipt(
	int SchemaVersion,
	string SourceSha256,
	string ExpectedCanonicalSha256,
	int ParentProcessId,
	long ParentProcessStartTimeUtcTicks,
	string Status,
	string? FailureType,
	string? FailureMessage,
	long UpdatedAtUtcTicks);

internal sealed class MaintenanceUpdaterPromotionReceiptStore
{
	internal const string FileName =
		"maintenance-updater-promotion-v1.json";
	internal const string FailedStatus = "failed";
	private const int CurrentSchemaVersion = 1;
	private const int MaximumReceiptBytes = 64 * 1024;
	private const int MaximumFailureMessageCharacters = 4096;
	private const string PendingStatus = "pending";
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
	};
	private readonly string _maintenanceRoot;
	private readonly TimeProvider _timeProvider;

	internal MaintenanceUpdaterPromotionReceiptStore(
		string maintenanceRoot,
		TimeProvider? timeProvider = null)
	{
		_maintenanceRoot = ManagedInstallationPaths.NormalizeDirectory(
			maintenanceRoot);
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	internal static bool IsOwnedFileName(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

		if (string.Equals(fileName, FileName, StringComparison.Ordinal))
		{
			return true;
		}

		const string temporarySuffix = ".writing";
		string temporaryPrefix = FileName + ".";
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

	internal async Task<MaintenanceUpdaterPromotionReceipt?> LoadAsync(
		CancellationToken cancellationToken = default)
	{
		string receiptPath = GetReceiptPath();

		if (!File.Exists(receiptPath))
		{
			return null;
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(_maintenanceRoot);
		UpdateTransaction.ThrowIfNotOrdinaryFile(receiptPath, mustExist: true);
		FileInfo receiptFile = new(receiptPath);

		if (receiptFile.Length > MaximumReceiptBytes)
		{
			throw new InvalidDataException(
				$"Maintenance updater promotion receipt '{receiptPath}' is too large.");
		}

		byte[] json = await File.ReadAllBytesAsync(
			receiptPath,
			cancellationToken);

		if (json.Length > MaximumReceiptBytes)
		{
			throw new InvalidDataException(
				$"Maintenance updater promotion receipt '{receiptPath}' is too large.");
		}

		MaintenanceUpdaterPromotionReceipt receipt;

		try
		{
			receipt = JsonSerializer.Deserialize<
				MaintenanceUpdaterPromotionReceipt>(json, JsonOptions) ??
				throw new InvalidDataException(
					"The maintenance updater promotion receipt is empty.");
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				$"Maintenance updater promotion receipt '{receiptPath}' is invalid.",
				exception);
		}

		try
		{
			Validate(receipt);
		}
		catch (Exception exception) when (
			exception is InvalidDataException or ArgumentException)
		{
			throw new InvalidDataException(
				$"Maintenance updater promotion receipt '{receiptPath}' does not " +
				"match the required contract.",
				exception);
		}

		return receipt;
	}

	internal async Task SavePendingAsync(
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		using UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			options.InstallRoot);
		await SavePendingWhileInstallLockHeldAsync(options, cancellationToken);
	}

	internal Task SavePendingWhileInstallLockHeldAsync(
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		return SaveAsync(
			CreateReceipt(options, PendingStatus, null, null),
			cancellationToken);
	}

	internal async Task<bool> SavePendingIfSnapshotMatchesWhileInstallLockHeldAsync(
		MaintenanceUpdaterPromotionReceipt expectedReceipt,
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(expectedReceipt);
		ArgumentNullException.ThrowIfNull(options);
		MaintenanceUpdaterPromotionReceipt? currentReceipt = await LoadAsync(
			cancellationToken);

		if (currentReceipt != expectedReceipt)
		{
			return false;
		}

		await SaveAsync(
			CreateReceipt(options, PendingStatus, null, null),
			cancellationToken);
		return true;
	}

	internal async Task<bool> IsCurrentWhileInstallLockHeldAsync(
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		MaintenanceUpdaterPromotionReceipt? receipt = await LoadAsync(
			cancellationToken);
		return (receipt is not null) && Matches(receipt, options);
	}

	internal async Task SaveFailureIfCurrentAsync(
		MaintenanceUpdaterPromotionOptions options,
		Exception exception,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(exception);
		using UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			options.InstallRoot);
		MaintenanceUpdaterPromotionReceipt? currentReceipt = await LoadAsync(
			CancellationToken.None);

		if ((currentReceipt is null) || !Matches(currentReceipt, options))
		{
			return;
		}

		await SaveAsync(
			CreateFailureReceipt(options, exception),
			cancellationToken);
	}

	internal Task SaveFailureWhileInstallLockHeldAsync(
		MaintenanceUpdaterPromotionOptions options,
		Exception exception,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(exception);
		return SaveAsync(
			CreateFailureReceipt(options, exception),
			cancellationToken);
	}

	internal async Task DeleteIfMatchesAsync(
		MaintenanceUpdaterPromotionOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		using UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(
			options.InstallRoot);
		MaintenanceUpdaterPromotionReceipt? receipt = await LoadAsync(
			cancellationToken);

		if ((receipt is null) || !Matches(receipt, options))
		{
			return;
		}

		string receiptPath = GetReceiptPath();
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(_maintenanceRoot);
		UpdateTransaction.ThrowIfNotOrdinaryFile(receiptPath, mustExist: true);
		File.Delete(receiptPath);
	}

	internal static bool IsPendingFor(
		MaintenanceUpdaterPromotionReceipt receipt,
		MaintenanceUpdaterPromotionOptions options)
	{
		ArgumentNullException.ThrowIfNull(receipt);
		ArgumentNullException.ThrowIfNull(options);
		return (receipt.Status == PendingStatus) && Matches(receipt, options);
	}

	private static bool Matches(
		MaintenanceUpdaterPromotionReceipt receipt,
		MaintenanceUpdaterPromotionOptions options)
	{
		return string.Equals(
				receipt.SourceSha256,
				options.SourceSha256,
				StringComparison.Ordinal) &&
			string.Equals(
				receipt.ExpectedCanonicalSha256,
				options.ExpectedCanonicalSha256,
				StringComparison.Ordinal) &&
			(receipt.ParentProcessId == options.ParentIdentity.ProcessId) &&
			(receipt.ParentProcessStartTimeUtcTicks ==
				options.ParentIdentity.ProcessStartTimeUtcTicks);
	}

	private static void Validate(MaintenanceUpdaterPromotionReceipt receipt)
	{
		if ((receipt.SchemaVersion != CurrentSchemaVersion) ||
			(receipt.ParentProcessId <= 0) ||
			(receipt.ParentProcessStartTimeUtcTicks <= 0) ||
			(receipt.UpdatedAtUtcTicks <= 0) ||
			(receipt.Status is not PendingStatus and not FailedStatus))
		{
			throw new InvalidDataException(
				"The maintenance updater promotion receipt contract is invalid.");
		}

		string normalizedSourceHash = ManagedInstallationPaths.NormalizeSha256(
			receipt.SourceSha256);
		string normalizedCanonicalHash =
			ManagedInstallationPaths.NormalizeSha256(
				receipt.ExpectedCanonicalSha256);

		if (!string.Equals(
				receipt.SourceSha256,
				normalizedSourceHash,
				StringComparison.Ordinal) ||
			!string.Equals(
				receipt.ExpectedCanonicalSha256,
				normalizedCanonicalHash,
				StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Maintenance updater promotion receipt hashes must be canonical.");
		}

		if ((receipt.Status == PendingStatus) &&
			((receipt.FailureType is not null) ||
				(receipt.FailureMessage is not null)))
		{
			throw new InvalidDataException(
				"A pending maintenance updater promotion cannot contain failure data.");
		}

		if ((receipt.Status == FailedStatus) &&
			(string.IsNullOrWhiteSpace(receipt.FailureType) ||
				string.IsNullOrWhiteSpace(receipt.FailureMessage)))
		{
			throw new InvalidDataException(
				"A failed maintenance updater promotion must contain failure data.");
		}
	}

	private MaintenanceUpdaterPromotionReceipt CreateReceipt(
		MaintenanceUpdaterPromotionOptions options,
		string status,
		string? failureType,
		string? failureMessage)
	{
		MaintenanceUpdaterPromotionReceipt receipt = new(
			CurrentSchemaVersion,
			options.SourceSha256,
			options.ExpectedCanonicalSha256,
			options.ParentIdentity.ProcessId,
			options.ParentIdentity.ProcessStartTimeUtcTicks,
			status,
			failureType,
			failureMessage,
			_timeProvider.GetUtcNow().UtcTicks);
		Validate(receipt);
		return receipt;
	}

	private MaintenanceUpdaterPromotionReceipt CreateFailureReceipt(
		MaintenanceUpdaterPromotionOptions options,
		Exception exception)
	{
		string failureDetail = exception.ToString();
		string rawFailureMessage = string.IsNullOrWhiteSpace(failureDetail)
			? "The promotion failed without an error message."
			: failureDetail;
		string failureMessage = rawFailureMessage.Length <=
			MaximumFailureMessageCharacters
			? rawFailureMessage
			: rawFailureMessage[..MaximumFailureMessageCharacters];
		return CreateReceipt(
			options,
			FailedStatus,
			exception.GetType().FullName ?? exception.GetType().Name,
			failureMessage);
	}

	private async Task SaveAsync(
		MaintenanceUpdaterPromotionReceipt receipt,
		CancellationToken cancellationToken)
	{
		Validate(receipt);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(_maintenanceRoot);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			_maintenanceRoot,
			mustExist: true);
		string receiptPath = GetReceiptPath();
		string temporaryPath = receiptPath + "." +
			Guid.NewGuid().ToString("N") + ".writing";

		try
		{
			UpdateTransaction.ThrowIfNotOrdinaryFile(
				temporaryPath,
				mustExist: false);
			byte[] json = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);

			if (json.Length > MaximumReceiptBytes)
			{
				throw new InvalidDataException(
					"The maintenance updater promotion receipt is too large.");
			}

			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await stream.WriteAsync(json, cancellationToken);
				await stream.FlushAsync(cancellationToken);
				stream.Flush(flushToDisk: true);
			}

			UpdateTransaction.ThrowIfNotOrdinaryFile(
				receiptPath,
				mustExist: false);
			File.Move(temporaryPath, receiptPath, overwrite: true);
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

	private string GetReceiptPath()
	{
		return Path.Combine(_maintenanceRoot, FileName);
	}
}
