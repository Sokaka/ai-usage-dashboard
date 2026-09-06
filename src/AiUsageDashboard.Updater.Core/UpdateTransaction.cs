using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.Updater.Core;

public enum UpdateTransactionState
{
	Prepared,
	Staged,
	ShutdownRequested,
	AppExited,
	Switching,
	Committed,
	RolledBack,
	Failed
}

public sealed class UpdateTransaction
{
	private sealed record TransactionReceipt(
		[property: JsonPropertyName("transactionId")] string TransactionId,
		[property: JsonPropertyName("state")]
		UpdateTransactionState State,
		[property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
		[property: JsonPropertyName("schemaVersion")] int? SchemaVersion = null,
		[property: JsonPropertyName("packageId")] string? PackageId = null,
		[property: JsonPropertyName("installRoot")] string? InstallRoot = null,
		[property: JsonPropertyName("originalPayload")] UpdatePayloadIdentity? OriginalPayload = null,
		[property: JsonPropertyName("targetPayload")] UpdatePayloadIdentity? TargetPayload = null);

	private const int CurrentReceiptSchemaVersion = 1;
	private const long MaximumReceiptSizeBytes = 64 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
	};
	private readonly Action<string, string> _replaceReceipt;
	private UpdatePayloadIdentity? _originalPayload;
	private UpdatePayloadIdentity? _targetPayload;

	public string InstallRootPath { get; }
	public string CurrentDirectoryPath { get; }
	public string PreviousDirectoryPath { get; }
	public string StagingDirectoryPath { get; }
	public string StateFilePath { get; }
	public string TransactionDirectoryPath { get; }
	public string TransactionId { get; }
	public UpdateTransactionState State { get; private set; }
	public bool HasPendingSwitch => (_targetPayload is not null) &&
		(State is UpdateTransactionState.Switching or UpdateTransactionState.Failed);
	internal UpdatePayloadIdentity? OriginalPayload => _originalPayload;
	internal UpdatePayloadIdentity? TargetPayload => _targetPayload;

	private UpdateTransaction(
		string installRootPath,
		string transactionId,
		Action<string, string>? replaceReceipt = null)
	{
		_replaceReceipt = replaceReceipt ?? ((source, destination) =>
			File.Move(source, destination, overwrite: true));
		InstallRootPath = installRootPath;
		TransactionId = transactionId;
		CurrentDirectoryPath = Path.Combine(InstallRootPath, "current");
		TransactionDirectoryPath = Path.Combine(
			InstallRootPath,
			"transactions",
			TransactionId);
		StagingDirectoryPath = Path.Combine(TransactionDirectoryPath, "staging");
		PreviousDirectoryPath = Path.Combine(TransactionDirectoryPath, "previous");
		StateFilePath = Path.Combine(TransactionDirectoryPath, "transaction.json");
		State = UpdateTransactionState.Prepared;
	}

	public static UpdateTransaction Create(
		string installRootPath,
		string? transactionId = null)
	{
		return CreateCore(installRootPath, transactionId, replaceReceipt: null);
	}

	internal static UpdateTransaction CreateCore(
		string installRootPath,
		string? transactionId,
		Action<string, string>? replaceReceipt)
	{
		string normalizedInstallRoot = NormalizeInstallRoot(installRootPath);
		string resolvedTransactionId = transactionId ?? Guid.NewGuid().ToString("N");
		ValidateTransactionId(resolvedTransactionId);

		UpdateTransaction transaction = new(
			normalizedInstallRoot,
			resolvedTransactionId,
			replaceReceipt);
		string transactionsRootPath = Path.Combine(
			normalizedInstallRoot,
			"transactions");

		if (File.Exists(normalizedInstallRoot))
		{
			throw new IOException("Install root is not a directory.");
		}

		ThrowIfUnsafeExistingDirectoryAncestry(normalizedInstallRoot);
		Directory.CreateDirectory(normalizedInstallRoot);
		ThrowIfUnsafeExistingDirectoryAncestry(normalizedInstallRoot);
		ThrowIfNotOrdinaryDirectory(normalizedInstallRoot, mustExist: true);

		if (File.Exists(transactionsRootPath))
		{
			throw new IOException("Transactions root is not a directory.");
		}

		ThrowIfUnsafeExistingDirectoryAncestry(transactionsRootPath);
		Directory.CreateDirectory(transactionsRootPath);
		ThrowIfUnsafeExistingDirectoryAncestry(transactionsRootPath);
		ThrowIfNotOrdinaryDirectory(transactionsRootPath, mustExist: true);

		if (Directory.Exists(transaction.TransactionDirectoryPath) ||
			File.Exists(transaction.TransactionDirectoryPath))
		{
			throw new IOException("Update transaction already exists.");
		}

		ThrowIfUnsafeExistingDirectoryAncestry(
			transaction.TransactionDirectoryPath);
		Directory.CreateDirectory(transaction.TransactionDirectoryPath);
		transaction.ValidateOwnedDirectoryChain();
		transaction.WriteState(UpdateTransactionState.Prepared);
		return transaction;
	}

	internal static UpdateTransaction Load(string installRootPath, string transactionId)
	{
		ValidateTransactionId(transactionId);
		UpdateTransaction transaction = new(NormalizeInstallRoot(installRootPath), transactionId);
		ThrowIfUnsafeExistingDirectoryAncestry(transaction.TransactionDirectoryPath);
		ThrowIfNotOrdinaryFile(transaction.StateFilePath, mustExist: true);
		using FileStream stream = File.OpenRead(transaction.StateFilePath);
		if (stream.Length > MaximumReceiptSizeBytes)
		{
			throw new InvalidDataException($"Transaction receipt exceeds the size limit: {transaction.StateFilePath}");
		}

		TransactionReceipt receipt;
		try
		{
			using JsonDocument document = JsonDocument.Parse(stream);
			ValidateUniqueProperties(document.RootElement, transaction.StateFilePath);
			receipt = document.RootElement.Deserialize<TransactionReceipt>(JsonOptions) ??
				throw new InvalidDataException($"Transaction receipt is empty: {transaction.StateFilePath}");
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"Cannot read transaction receipt: {transaction.StateFilePath}", exception);
		}

		ValidateReceiptOwnership(receipt, transaction);
		ValidateSwitchIntent(receipt, transaction.StateFilePath);
		transaction.State = receipt.State;
		transaction._originalPayload = receipt.OriginalPayload;
		transaction._targetPayload = receipt.TargetPayload;
		return transaction;
	}

	private static void ValidateReceiptOwnership(TransactionReceipt receipt, UpdateTransaction transaction)
	{
		if ((receipt.TransactionId != transaction.TransactionId) || (!Enum.IsDefined(receipt.State)))
		{
			throw new InvalidDataException($"Transaction identity or state does not match its directory: {transaction.StateFilePath}");
		}

		if (receipt.SchemaVersion is null)
		{
			if ((receipt.PackageId is not null) || (receipt.InstallRoot is not null) ||
				(receipt.OriginalPayload is not null) || (receipt.TargetPayload is not null) ||
				(receipt.State == UpdateTransactionState.Switching) ||
				((receipt.State == UpdateTransactionState.Failed) && Directory.Exists(transaction.PreviousDirectoryPath)))
			{
				throw new InvalidDataException($"Legacy transaction lacks recovery ownership proof and requires manual layout inspection: {transaction.StateFilePath}");
			}
		}
		else if ((receipt.SchemaVersion != CurrentReceiptSchemaVersion) ||
			(receipt.PackageId != UpdateManifest.ExpectedPackageId) ||
			!string.Equals(receipt.InstallRoot, transaction.InstallRootPath, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"Transaction receipt ownership or schema does not match this installation: {transaction.StateFilePath}");
		}
	}

	private static void ValidateSwitchIntent(TransactionReceipt receipt, string receiptPath)
	{
		receipt.OriginalPayload?.Validate(receiptPath);
		receipt.TargetPayload?.Validate(receiptPath);
		if (((receipt.OriginalPayload is not null) && (receipt.TargetPayload is null)) ||
			((receipt.SchemaVersion is not null) &&
				(receipt.State is UpdateTransactionState.Switching or UpdateTransactionState.Committed or UpdateTransactionState.RolledBack) &&
				(receipt.TargetPayload is null)) ||
			((receipt.TargetPayload is not null) &&
				(receipt.State is UpdateTransactionState.Prepared or UpdateTransactionState.Staged or
				UpdateTransactionState.ShutdownRequested or UpdateTransactionState.AppExited)))
		{
			throw new InvalidDataException($"Transaction switch intent is inconsistent: {receiptPath}");
		}
	}

	private static void ValidateUniqueProperties(JsonElement element, string receiptPath)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!names.Add(property.Name))
			{
				throw new InvalidDataException($"Duplicate transaction receipt field '{property.Name}': {receiptPath}");
			}

			ValidateUniqueProperties(property.Value, receiptPath);
		}
	}

	internal void BeginSwitch()
	{
		if (State != UpdateTransactionState.AppExited)
		{
			throw new InvalidOperationException($"Cannot prepare switch in state {State}: {TransactionId}");
		}

		UpdatePayloadIdentity? originalPayload = Directory.Exists(CurrentDirectoryPath)
			? UpdatePayloadIdentity.Read(CurrentDirectoryPath)
			: null;
		UpdatePayloadIdentity targetPayload = UpdatePayloadIdentity.Read(StagingDirectoryPath);
		_originalPayload = originalPayload;
		_targetPayload = targetPayload;
		TransitionTo(UpdateTransactionState.Switching);
	}

	internal void ValidateOwnedDirectoryChain()
	{
		ThrowIfUnsafeExistingDirectoryAncestry(InstallRootPath);
		ThrowIfUnsafeExistingDirectoryAncestry(
			Path.Combine(InstallRootPath, "transactions"));
		ThrowIfUnsafeExistingDirectoryAncestry(TransactionDirectoryPath);
		ThrowIfUnsafeExistingDirectoryAncestry(CurrentDirectoryPath);
		ThrowIfUnsafeExistingDirectoryAncestry(StagingDirectoryPath);
		ThrowIfUnsafeExistingDirectoryAncestry(PreviousDirectoryPath);
		ThrowIfNotOrdinaryDirectory(InstallRootPath, mustExist: true);
		ThrowIfNotOrdinaryDirectory(
			Path.Combine(InstallRootPath, "transactions"),
			mustExist: true);
		ThrowIfNotOrdinaryDirectory(TransactionDirectoryPath, mustExist: true);
		ThrowIfNotOrdinaryDirectory(CurrentDirectoryPath, mustExist: false);
		ThrowIfNotOrdinaryDirectory(StagingDirectoryPath, mustExist: false);
		ThrowIfNotOrdinaryDirectory(PreviousDirectoryPath, mustExist: false);
	}

	internal static void ThrowIfNotOrdinaryDirectory(
		string directoryPath,
		bool mustExist)
	{
		if (File.Exists(directoryPath))
		{
			throw new IOException("Updater-owned path is not a directory.");
		}

		if (!Directory.Exists(directoryPath))
		{
			if (mustExist)
			{
				throw new DirectoryNotFoundException(
					"Updater-owned directory was not found.");
			}

			return;
		}

		if ((File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0)
		{
			throw new InvalidDataException(
				"Updater-owned directories cannot be reparse points.");
		}
	}

	internal static void ThrowIfNotOrdinaryFile(
		string filePath,
		bool mustExist)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		if (Directory.Exists(filePath))
		{
			throw new IOException("Updater-owned path is not a file.");
		}

		if (!File.Exists(filePath))
		{
			if (mustExist)
			{
				throw new FileNotFoundException(
					"Updater-owned file was not found.",
					filePath);
			}

			return;
		}

		FileAttributes attributes = File.GetAttributes(filePath);
		if ((attributes &
			(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
		{
			throw new InvalidDataException(
				"Updater-owned files cannot be directories or reparse points.");
		}
	}

	internal static void ThrowIfUnsafeExistingDirectoryAncestry(
		string directoryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
		DirectoryInfo? current = new(Path.GetFullPath(directoryPath));

		while (current is not null)
		{
			current.Refresh();
			if (File.Exists(current.FullName))
			{
				throw new IOException(
					"Install root ancestry contains a file.");
			}

			if (current.Exists &&
				((current.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				throw new InvalidDataException(
					"Install root cannot pass through a reparse-point directory.");
			}

			current = current.Parent;
		}
	}

	public void TransitionTo(UpdateTransactionState nextState)
	{
		if ((nextState == UpdateTransactionState.RolledBack) && (_targetPayload is null))
		{
			throw new InvalidOperationException($"Cannot record rollback without a switch intent: {TransactionId}");
		}

		if ((nextState == UpdateTransactionState.Switching) && (_targetPayload is null))
		{
			throw new InvalidOperationException($"Switch intent must be prepared before renaming payloads: {TransactionId}");
		}

		if (!CanTransition(State, nextState))
		{
			throw new InvalidOperationException(
				$"Invalid update transaction transition: {State} -> {nextState}.");
		}

		WriteState(nextState);
		State = nextState;
	}

	private static bool CanTransition(
		UpdateTransactionState currentState,
		UpdateTransactionState nextState)
	{
		return currentState switch
		{
			UpdateTransactionState.Prepared =>
				nextState is UpdateTransactionState.Staged or
				UpdateTransactionState.Failed,
			UpdateTransactionState.Staged =>
				nextState is UpdateTransactionState.ShutdownRequested or
				UpdateTransactionState.Failed,
			UpdateTransactionState.ShutdownRequested =>
				nextState is UpdateTransactionState.AppExited or
				UpdateTransactionState.Failed,
			UpdateTransactionState.AppExited =>
				nextState is UpdateTransactionState.Switching or
				UpdateTransactionState.Failed,
			UpdateTransactionState.Switching =>
				nextState is UpdateTransactionState.Committed or
				UpdateTransactionState.RolledBack or
				UpdateTransactionState.Failed,
			UpdateTransactionState.Failed => nextState == UpdateTransactionState.RolledBack,
			_ => false
		};
	}

	internal static string NormalizeInstallRoot(string installRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(installRootPath);
		string fullPath = Path.GetFullPath(installRootPath)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string? volumeRoot = Path.GetPathRoot(fullPath);

		if (string.IsNullOrEmpty(fullPath) ||
			string.Equals(
				fullPath,
				volumeRoot?.TrimEnd(
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(
				"Install root cannot be a volume root.",
				nameof(installRootPath));
		}

		return fullPath;
	}

	private static void ValidateTransactionId(string transactionId)
	{
		if (string.IsNullOrWhiteSpace(transactionId) ||
			(transactionId.Length > 64) ||
			transactionId.Any(character =>
				!char.IsAsciiLetterOrDigit(character) &&
				(character != '-') &&
				(character != '_')))
		{
			throw new ArgumentException(
				"Transaction ID contains unsupported characters.",
				nameof(transactionId));
		}
	}

	private void WriteState(UpdateTransactionState state)
	{
		ValidateOwnedDirectoryChain();
		ThrowIfNotOrdinaryFile(StateFilePath, mustExist: false);
		TransactionReceipt receipt = new(
			TransactionId,
			state,
			DateTimeOffset.UtcNow,
			CurrentReceiptSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			InstallRootPath,
			_originalPayload,
			_targetPayload);
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
		string temporaryPath = Path.Combine(TransactionDirectoryPath, $"receipt-{Guid.NewGuid():N}.tmp");
		using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
		{
			stream.Write(json);
			stream.Flush(flushToDisk: true);
		}

		// temp 是未提交的證據；寫入或 rename 失敗時保留，復原只讀已提交的 receipt。
		ValidateOwnedDirectoryChain();
		ThrowIfNotOrdinaryFile(StateFilePath, mustExist: false);
		_replaceReceipt(temporaryPath, StateFilePath);
	}
}
