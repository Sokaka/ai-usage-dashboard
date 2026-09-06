using System.Security.Cryptography;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed class AntigravityMachineSetupPrivateTransaction :
	IAsyncDisposable
{
	private const int HmacKeyBytes = 32;
	private const int MaximumProfileBytes = 1024 * 1024;

	private bool _isDisposed;
	private bool _isKeyCreated;
	private bool _isPreserved;
	private bool _isProfileCreated;
	private bool _isProfileWriteAttempted;
	private bool _isTemporaryFileCreated;
	private string? _temporaryFilePath;

	internal bool CleanupSucceeded { get; private set; }

	internal string KeyPath { get; }

	internal string ProfilePath { get; }

	private AntigravityMachineSetupPrivateTransaction(
		string keyPath,
		string profilePath)
	{
		KeyPath = keyPath;
		ProfilePath = profilePath;
	}

	public ValueTask DisposeAsync()
	{
		if (_isDisposed)
		{
			return ValueTask.CompletedTask;
		}

		if (_isPreserved)
		{
			CleanupSucceeded =
				_isKeyCreated &&
				_isProfileCreated &&
				!_isTemporaryFileCreated &&
				AntigravityPrivateKeyAcl.IsPrivateFile(KeyPath) &&
				AntigravityPrivateKeyAcl.IsPrivateFile(ProfilePath);
			_isDisposed = true;
			return ValueTask.CompletedTask;
		}

		bool temporaryCleanupSucceeded = TryDeleteOwnedFile(
			_temporaryFilePath,
			ref _isTemporaryFileCreated);
		bool profileCleanupSucceeded = TryDeleteOwnedFile(
			ProfilePath,
			ref _isProfileCreated);
		bool keyCleanupSucceeded = TryDeleteOwnedFile(
			KeyPath,
			ref _isKeyCreated);
		CleanupSucceeded =
			temporaryCleanupSucceeded &&
			profileCleanupSucceeded &&
			keyCleanupSucceeded;
		_isDisposed = CleanupSucceeded;
		return ValueTask.CompletedTask;
	}

	internal static async Task<AntigravityMachineSetupPrivateTransaction>
		CreateAsync(
			string absolutePrivateRoot,
			string contractId,
			CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string privateRoot = ValidatePrivateRoot(absolutePrivateRoot);
		ValidateContractId(contractId);

		if (!AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privateRoot,
				out _,
				out AntigravityPrivateKeyDirectoryFailureReason failureReason) ||
			!AntigravityPrivateKeyAcl.IsPrivateDirectory(privateRoot))
		{
			throw new IOException(
				$"The AGY private setup directory was rejected at stage: {failureReason}.");
		}

		string transactionId = Guid.NewGuid().ToString("N");
		string fileStem = $"agy-{contractId}-{transactionId}";
		string keyPath = Path.Combine(privateRoot, $"{fileStem}.key");
		string profilePath = Path.Combine(
			privateRoot,
			$"{fileStem}.profile.json");

		if (File.Exists(keyPath) ||
			Directory.Exists(keyPath) ||
			File.Exists(profilePath) ||
			Directory.Exists(profilePath))
		{
			throw new IOException(
				"The AGY private setup transaction refused to overwrite an existing path.");
		}

		AntigravityMachineSetupPrivateTransaction transaction = new(
			keyPath,
			profilePath);

		try
		{
			await transaction.CreateKeyAsync(cancellationToken);
			return transaction;
		}
		catch (Exception exception)
		{
			await transaction.DisposeAsync();

			if (!transaction.CleanupSucceeded)
			{
				throw new IOException(
					"The AGY private setup transaction failed to clean up its partial key.",
					exception);
			}

			throw;
		}
	}

	internal void MarkApproved()
	{
		EnsureNotDisposed();

		if (!_isKeyCreated ||
			!_isProfileCreated ||
			_isTemporaryFileCreated ||
			!AntigravityPrivateKeyAcl.IsPrivateFile(KeyPath) ||
			!AntigravityPrivateKeyAcl.IsPrivateFile(ProfilePath))
		{
			throw new InvalidOperationException(
				"The AGY private setup transaction is not ready for approval.");
		}

		_isPreserved = true;
	}

	internal async Task WriteProfileAsync(
		byte[] profileBytes,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profileBytes);
		cancellationToken.ThrowIfCancellationRequested();
		EnsureNotDisposed();

		if (_isPreserved || _isProfileWriteAttempted)
		{
			throw new InvalidOperationException(
				"The AGY private setup profile can be written only once.");
		}

		if ((profileBytes.Length <= 0) ||
			(profileBytes.Length > MaximumProfileBytes))
		{
			throw new ArgumentOutOfRangeException(
				nameof(profileBytes),
				$"The AGY private setup profile must contain 1-{MaximumProfileBytes} bytes.");
		}

		_isProfileWriteAttempted = true;

		if (!AntigravityPrivateKeyAcl.IsPrivateDirectory(
				Path.GetDirectoryName(ProfilePath) ?? string.Empty) ||
			File.Exists(ProfilePath) ||
			Directory.Exists(ProfilePath))
		{
			throw new IOException(
				"The AGY private setup profile path is not available for a create-new commit.");
		}

		byte[] expectedBytes = profileBytes.ToArray();
		_temporaryFilePath = Path.Combine(
			Path.GetDirectoryName(ProfilePath)!,
			$".{Path.GetFileName(ProfilePath)}.{Guid.NewGuid():N}.tmp");

		try
		{
			await using (FileStream createStream = new(
				_temporaryFilePath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				_isTemporaryFileCreated = true;
				await createStream.FlushAsync(cancellationToken);
				createStream.Flush(flushToDisk: true);
			}

			if (!AntigravityPrivateKeyAcl.TryProtectNewFile(
					_temporaryFilePath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(_temporaryFilePath))
			{
				throw new IOException(
					"The AGY private setup profile staging ACL was rejected.");
			}

			await using (FileStream writeStream = new(
				_temporaryFilePath,
				FileMode.Open,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await writeStream.WriteAsync(expectedBytes, cancellationToken);
				await writeStream.FlushAsync(cancellationToken);
				writeStream.Flush(flushToDisk: true);
			}

			if (!await HasExactPrivateBytesAsync(
					_temporaryFilePath,
					expectedBytes,
					cancellationToken))
			{
				throw new IOException(
					"The AGY private setup profile staging verification failed.");
			}

			cancellationToken.ThrowIfCancellationRequested();

			if (!AntigravityPrivateKeyAcl.IsPrivateDirectory(
					Path.GetDirectoryName(ProfilePath)!) ||
				File.Exists(ProfilePath) ||
				Directory.Exists(ProfilePath))
			{
				throw new IOException(
					"The AGY private setup profile destination changed before commit.");
			}

			File.Move(
				_temporaryFilePath,
				ProfilePath,
				overwrite: false);
			_isTemporaryFileCreated = false;
			_temporaryFilePath = null;
			_isProfileCreated = true;

			if (!await HasExactPrivateBytesAsync(
					ProfilePath,
					expectedBytes,
					CancellationToken.None))
			{
				throw new IOException(
					"The AGY private setup profile commit verification failed.");
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(expectedBytes);
		}
	}

	private static async Task<bool> HasExactPrivateBytesAsync(
		string path,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		byte[]? observedBytes = null;

		try
		{
			if (!AntigravityPrivateKeyAcl.IsPrivateFile(path))
			{
				return false;
			}

			FileInfo before = new(path);
			before.Refresh();

			if (!before.Exists ||
				(before.Length != expectedBytes.Length) ||
				((before.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
			{
				return false;
			}

			long expectedLength = before.Length;
			DateTime expectedCreationTimeUtc = before.CreationTimeUtc;
			DateTime expectedLastWriteTimeUtc = before.LastWriteTimeUtc;
			FileAttributes expectedAttributes = before.Attributes;
			observedBytes = new byte[checked((int)expectedLength)];

			await using (FileStream stream = new(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await stream.ReadExactlyAsync(observedBytes, cancellationToken);

				if (stream.ReadByte() != -1)
				{
					return false;
				}
			}

			FileInfo after = new(path);
			after.Refresh();

			return
				after.Exists &&
				(after.Length == expectedLength) &&
				(after.CreationTimeUtc == expectedCreationTimeUtc) &&
				(after.LastWriteTimeUtc == expectedLastWriteTimeUtc) &&
				(after.Attributes == expectedAttributes) &&
				((after.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0) &&
				AntigravityPrivateKeyAcl.IsPrivateFile(path) &&
				CryptographicOperations.FixedTimeEquals(
					observedBytes,
					expectedBytes.Span);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (observedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(observedBytes);
			}
		}
	}

	private static bool IsSafeContractId(string contractId)
	{
		return AntigravityReviewedPackageManifestJson.IsStableId(contractId);
	}

	private static bool TryDeleteOwnedFile(
		string? path,
		ref bool isOwned)
	{
		if (!isOwned)
		{
			return true;
		}

		try
		{
			if (path is null)
			{
				return false;
			}

			if (!File.Exists(path))
			{
				if (Directory.Exists(path))
				{
					return false;
				}

				isOwned = false;
				return true;
			}

			FileAttributes attributes = File.GetAttributes(path);

			if ((attributes &
				(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
			{
				return false;
			}

			File.Delete(path);

			if (File.Exists(path) || Directory.Exists(path))
			{
				return false;
			}

			isOwned = false;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string ValidatePrivateRoot(string absolutePrivateRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absolutePrivateRoot);

		if (!OperatingSystem.IsWindows() ||
			!Path.IsPathFullyQualified(absolutePrivateRoot))
		{
			throw new ArgumentException(
				"The AGY private setup root must be an absolute Windows path.",
				nameof(absolutePrivateRoot));
		}

		return Path.GetFullPath(absolutePrivateRoot);
	}

	private static void ValidateContractId(string contractId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(contractId);

		if (!IsSafeContractId(contractId))
		{
			throw new ArgumentException(
				"The AGY private setup contract ID is unsafe.",
				nameof(contractId));
		}
	}

	private async Task CreateKeyAsync(CancellationToken cancellationToken)
	{
		byte[] hmacKey = RandomNumberGenerator.GetBytes(HmacKeyBytes);

		try
		{
			await using (FileStream createStream = new(
				KeyPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				_isKeyCreated = true;
				await createStream.FlushAsync(cancellationToken);
				createStream.Flush(flushToDisk: true);
			}

			if (!AntigravityPrivateKeyAcl.TryProtectNewFile(KeyPath) ||
				!AntigravityPrivateKeyAcl.IsPrivateFile(KeyPath))
			{
				throw new IOException(
					"The AGY private setup key ACL was rejected.");
			}

			await using (FileStream writeStream = new(
				KeyPath,
				FileMode.Open,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await writeStream.WriteAsync(hmacKey, cancellationToken);
				await writeStream.FlushAsync(cancellationToken);
				writeStream.Flush(flushToDisk: true);
			}

			if (!await HasExactPrivateBytesAsync(
					KeyPath,
					hmacKey,
					cancellationToken))
			{
				throw new IOException(
					"The AGY private setup key verification failed.");
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(hmacKey);
		}
	}

	private void EnsureNotDisposed()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);
	}
}
