using System.IO.Compression;
using System.Security.Cryptography;

namespace AiUsageDashboard.Updater.Core;

public sealed record StagedUpdatePackage(
	string PayloadDirectoryPath,
	string ApplicationExecutablePath,
	int EntryCount,
	long ExpandedSizeBytes);

public sealed class UpdatePackageStager
{
	public const int DefaultMaximumEntryCount = 4096;
	public const long DefaultMaximumArchiveSizeBytes = 512L * 1024 * 1024;
	public const long DefaultMaximumEntrySizeBytes = 512L * 1024 * 1024;
	public const long DefaultMaximumExpandedSizeBytes = 1024L * 1024 * 1024;

	private const string ApplicationExecutableRelativePath =
		"app\\AiUsageDashboard.App.exe";
	private const string ArchiveRootName = "AiUsageDashboard";
	private const string PayloadDirectoryName = "app";
	private const string RootGuideFileName = "使用說明.md";
	private readonly long _maximumArchiveSizeBytes;
	private readonly int _maximumEntryCount;
	private readonly long _maximumEntrySizeBytes;
	private readonly long _maximumExpandedSizeBytes;

	public UpdatePackageStager()
		: this(
			DefaultMaximumEntryCount,
			DefaultMaximumArchiveSizeBytes,
			DefaultMaximumEntrySizeBytes,
			DefaultMaximumExpandedSizeBytes)
	{
	}

	internal UpdatePackageStager(
		int maximumEntryCount,
		long maximumArchiveSizeBytes,
		long maximumEntrySizeBytes,
		long maximumExpandedSizeBytes)
	{
		if ((maximumEntryCount <= 0) ||
			(maximumArchiveSizeBytes <= 0) ||
			(maximumEntrySizeBytes <= 0) ||
			(maximumExpandedSizeBytes <= 0))
		{
			throw new ArgumentOutOfRangeException(
				nameof(maximumEntryCount),
				"Package limits must be positive.");
		}

		_maximumEntryCount = maximumEntryCount;
		_maximumArchiveSizeBytes = maximumArchiveSizeBytes;
		_maximumEntrySizeBytes = maximumEntrySizeBytes;
		_maximumExpandedSizeBytes = maximumExpandedSizeBytes;
	}

	public async Task<StagedUpdatePackage> StageAsync(
		string packagePath,
		UpdateManifest manifest,
		UpdateTransaction transaction,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(transaction);

		if (transaction.State != UpdateTransactionState.Prepared)
		{
			throw new InvalidOperationException(
				"Only a prepared update transaction can stage a package.");
		}

		manifest.Validate();
		string fullPackagePath = Path.GetFullPath(packagePath);
		if (!File.Exists(fullPackagePath))
		{
			throw new FileNotFoundException(
				"Update package was not found.",
				fullPackagePath);
		}
		ValidatePackageFile(fullPackagePath);

		await using (FileStream packageStream = new(
			fullPackagePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan))
		{
			ValidatePackageFile(fullPackagePath);
			if ((packageStream.Length != manifest.ArchiveSizeBytes) ||
				(packageStream.Length > _maximumArchiveSizeBytes))
			{
				throw new InvalidDataException(
					"Update package size does not match the trusted manifest or exceeds the limit.");
			}

			byte[] actualHash = await SHA256.HashDataAsync(
				packageStream,
				cancellationToken);
			byte[] expectedHash = Convert.FromHexString(manifest.ArchiveSha256);
			if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
			{
				throw new InvalidDataException(
					"Update package SHA-256 does not match the trusted manifest.");
			}

			packageStream.Position = 0;
			ValidatePackageFile(fullPackagePath);
			try
			{
				return await ExtractVerifiedPackageAsync(
					packageStream,
					manifest,
					transaction,
					cancellationToken);
			}
			catch
			{
				if (transaction.State == UpdateTransactionState.Prepared)
				{
					transaction.TransitionTo(UpdateTransactionState.Failed);
				}

				throw;
			}
		}
	}

	private async Task<StagedUpdatePackage> ExtractVerifiedPackageAsync(
		Stream packageStream,
		UpdateManifest manifest,
		UpdateTransaction transaction,
		CancellationToken cancellationToken)
	{
		using ZipArchive archive = new(
			packageStream,
			ZipArchiveMode.Read,
			leaveOpen: true);
		List<PlannedEntry> plannedEntries = ValidateEntries(archive);
		CreateAndValidateDirectory(
			transaction,
			transaction.StagingDirectoryPath);

		long extractedSize = 0;
		foreach (PlannedEntry plannedEntry in plannedEntries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string destinationPath = Path.Combine(
				transaction.StagingDirectoryPath,
				plannedEntry.RelativePath);

			if (plannedEntry.IsDirectory)
			{
				CreateAndValidateDirectory(transaction, destinationPath);
				continue;
			}

			string? parentPath = Path.GetDirectoryName(destinationPath);
			if (string.IsNullOrEmpty(parentPath))
			{
				throw new InvalidDataException(
					"Update package entry has no destination directory.");
			}

			CreateAndValidateDirectory(transaction, parentPath);
			await using Stream source = plannedEntry.Entry.Open();
			await using (FileStream destination = OpenValidatedDestinationFile(
				transaction,
				destinationPath))
			{
				extractedSize += await CopyBoundedAsync(
					source,
					destination,
					plannedEntry.Entry.Length,
					_maximumExpandedSizeBytes - extractedSize,
					cancellationToken);
				await destination.FlushAsync(cancellationToken);
				ValidateDestinationFile(
					transaction,
					destinationPath,
					mustExist: true);
			}

			ValidateDestinationFile(
				transaction,
				destinationPath,
				mustExist: true);
		}

		string applicationExecutablePath = Path.Combine(
			transaction.StagingDirectoryPath,
			ApplicationExecutableRelativePath);
		ValidateDestinationFile(
			transaction,
			applicationExecutablePath,
			mustExist: true);

		string installedManifestPath = Path.Combine(
			transaction.StagingDirectoryPath,
			UpdateManifest.InstalledFileName);
		ValidateDestinationFile(
			transaction,
			installedManifestPath,
			mustExist: false);
		await manifest.WriteAsync(
			installedManifestPath,
			cancellationToken);
		ValidateDestinationFile(
			transaction,
			installedManifestPath,
			mustExist: true);
		transaction.ValidateOwnedDirectoryChain();
		transaction.TransitionTo(UpdateTransactionState.Staged);

		return new StagedUpdatePackage(
			transaction.StagingDirectoryPath,
			applicationExecutablePath,
			plannedEntries.Count,
			extractedSize);
	}

	private static void CreateAndValidateDirectory(
		UpdateTransaction transaction,
		string directoryPath)
	{
		ValidateDestinationPath(transaction, directoryPath);
		transaction.ValidateOwnedDirectoryChain();
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(directoryPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			directoryPath,
			mustExist: false);
		Directory.CreateDirectory(directoryPath);
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(directoryPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			directoryPath,
			mustExist: true);
		transaction.ValidateOwnedDirectoryChain();
	}

	private static FileStream OpenValidatedDestinationFile(
		UpdateTransaction transaction,
		string destinationPath)
	{
		ValidateDestinationFile(
			transaction,
			destinationPath,
			mustExist: false);
		FileStream destination = new(
			destinationPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);

		try
		{
			ValidateDestinationFile(
				transaction,
				destinationPath,
				mustExist: true);
			return destination;
		}
		catch
		{
			destination.Dispose();
			throw;
		}
	}

	private static void ValidateDestinationFile(
		UpdateTransaction transaction,
		string destinationPath,
		bool mustExist)
	{
		ValidateDestinationPath(transaction, destinationPath);
		string? parentPath = Path.GetDirectoryName(destinationPath);

		if (string.IsNullOrEmpty(parentPath))
		{
			throw new InvalidDataException(
				"Update package destination has no parent directory.");
		}

		transaction.ValidateOwnedDirectoryChain();
		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			parentPath,
			mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(destinationPath, mustExist);
	}

	private static void ValidateDestinationPath(
		UpdateTransaction transaction,
		string destinationPath)
	{
		string stagingRoot = Path.GetFullPath(transaction.StagingDirectoryPath)
			.TrimEnd(
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar);
		string resolvedDestination = Path.GetFullPath(destinationPath);
		string stagingRootPrefix =
			stagingRoot + Path.DirectorySeparatorChar;

		if (!string.Equals(
				resolvedDestination,
				stagingRoot,
				StringComparison.OrdinalIgnoreCase) &&
			!resolvedDestination.StartsWith(
				stagingRootPrefix,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"Update package destination escapes the staging directory.");
		}
	}

	private static void ValidatePackageFile(string packagePath)
	{
		string? parentPath = Path.GetDirectoryName(packagePath);

		if (string.IsNullOrEmpty(parentPath))
		{
			throw new InvalidDataException(
				"Update package path has no parent directory.");
		}

		UpdateTransaction.ThrowIfUnsafeExistingDirectoryAncestry(parentPath);
		UpdateTransaction.ThrowIfNotOrdinaryDirectory(
			parentPath,
			mustExist: true);
		UpdateTransaction.ThrowIfNotOrdinaryFile(packagePath, mustExist: true);
	}

	private List<PlannedEntry> ValidateEntries(ZipArchive archive)
	{
		if ((archive.Entries.Count == 0) ||
			(archive.Entries.Count > _maximumEntryCount))
		{
			throw new InvalidDataException(
				"Update package entry count is invalid or exceeds the limit.");
		}

		HashSet<string> archivePaths = new(StringComparer.Ordinal);
		Dictionary<string, string> pathCasing = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, bool> pathKinds = new(StringComparer.OrdinalIgnoreCase);
		List<PlannedEntry> plannedEntries = [];
		long expandedSize = 0;

		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			PlannedEntry? plannedEntry = PlanEntry(
				entry,
				archivePaths,
				pathCasing,
				pathKinds);
			if (plannedEntry is null)
			{
				continue;
			}

			if ((entry.Length < 0) || (entry.Length > _maximumEntrySizeBytes))
			{
				throw new InvalidDataException(
					"Update package entry exceeds the expanded-size limit.");
			}

			if (expandedSize > (_maximumExpandedSizeBytes - entry.Length))
			{
				throw new InvalidDataException(
					"Update package exceeds the total expanded-size limit.");
			}

			expandedSize += entry.Length;
			plannedEntries.Add(plannedEntry);
		}

		if (!plannedEntries.Any(entry =>
			!entry.IsDirectory &&
			string.Equals(
				entry.RelativePath,
				ApplicationExecutableRelativePath,
				StringComparison.Ordinal)))
		{
			throw new InvalidDataException(
				"Update package does not contain the application executable.");
		}

		return plannedEntries;
	}

	private static PlannedEntry? PlanEntry(
		ZipArchiveEntry entry,
		HashSet<string> archivePaths,
		Dictionary<string, string> pathCasing,
		Dictionary<string, bool> pathKinds)
	{
		string originalPath = entry.FullName;
		if (string.IsNullOrEmpty(originalPath) ||
			originalPath.Contains('\0', StringComparison.Ordinal) ||
			originalPath.StartsWith("/", StringComparison.Ordinal) ||
			originalPath.StartsWith("\\", StringComparison.Ordinal))
		{
			throw new InvalidDataException("Update package contains a rooted or empty path.");
		}

		string normalizedPath = originalPath.Replace('\\', '/');
		if ((normalizedPath.Length >= 2) &&
			char.IsAsciiLetter(normalizedPath[0]) &&
			(normalizedPath[1] == ':'))
		{
			throw new InvalidDataException("Update package contains a rooted path.");
		}

		bool isDirectory = normalizedPath.EndsWith("/", StringComparison.Ordinal);
		string pathWithoutTrailingSeparator = normalizedPath.TrimEnd('/');
		string[] segments = pathWithoutTrailingSeparator.Split(
			'/',
			StringSplitOptions.None);

		if (segments.Any(segment =>
			string.IsNullOrEmpty(segment) ||
			(segment == ".") ||
			(segment == "..") ||
			!IsSafeWindowsPathSegment(segment)))
		{
			throw new InvalidDataException(
				"Update package contains an unsafe path segment.");
		}

		ThrowIfReparseEntry(entry);

		if (!string.Equals(segments[0], ArchiveRootName, StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update package must contain exactly the expected archive root.");
		}

		if (segments.Length == 1)
		{
			if (!isDirectory)
			{
				throw new InvalidDataException(
					"Update package archive root must be a directory.");
			}

			RegisterArchivePath(
				pathWithoutTrailingSeparator,
				isDirectory: true,
				archivePaths,
				pathCasing,
				pathKinds);
			return null;
		}

		if (segments.Length == 2)
		{
			bool isPayloadDirectory = string.Equals(
				segments[1],
				PayloadDirectoryName,
				StringComparison.Ordinal);
			bool isRootGuide = string.Equals(
				segments[1],
				RootGuideFileName,
				StringComparison.Ordinal);

			if ((isPayloadDirectory && !isDirectory) ||
				(isRootGuide && isDirectory) ||
				(!isPayloadDirectory && !isRootGuide))
			{
				throw new InvalidDataException(
					"Update package root contains an unexpected entry.");
			}
		}
		else if (!string.Equals(
			segments[1],
			PayloadDirectoryName,
			StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update package entries must be inside AiUsageDashboard/app.");
		}

		RegisterArchivePath(
			pathWithoutTrailingSeparator,
			isDirectory,
			archivePaths,
			pathCasing,
			pathKinds);

		string relativePath = Path.Combine(segments.Skip(1).ToArray());
		return new PlannedEntry(entry, relativePath, isDirectory);
	}

	private static void RegisterArchivePath(
		string path,
		bool isDirectory,
		HashSet<string> archivePaths,
		Dictionary<string, string> pathCasing,
		Dictionary<string, bool> pathKinds)
	{
		if (!archivePaths.Add(path))
		{
			throw new InvalidDataException(
				"Update package contains a duplicate entry.");
		}

		if (pathCasing.TryGetValue(path, out string? existingCasing) &&
			!string.Equals(existingCasing, path, StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"Update package contains a case-colliding entry.");
		}

		pathCasing[path] = path;
		string[] segments = path.Split('/');
		for (int index = 1; index <= segments.Length; index++)
		{
			string partialPath = string.Join('/', segments.Take(index));
			bool partialIsDirectory = (index < segments.Length) || isDirectory;

			if (pathCasing.TryGetValue(
				partialPath,
				out string? existingPartialCasing) &&
				!string.Equals(
					existingPartialCasing,
					partialPath,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"Update package contains a case-colliding path.");
			}

			pathCasing[partialPath] = partialPath;
			if (pathKinds.TryGetValue(partialPath, out bool existingIsDirectory))
			{
				if (!existingIsDirectory || !partialIsDirectory)
				{
					throw new InvalidDataException(
						"Update package contains a file/directory collision.");
				}
			}
			else
			{
				pathKinds[partialPath] = partialIsDirectory;
			}
		}
	}

	private static bool IsSafeWindowsPathSegment(string segment)
	{
		if (segment.EndsWith(' ') || segment.EndsWith('.'))
		{
			return false;
		}

		if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			return false;
		}

		string nameWithoutExtension = segment.Split('.')[0];
		return !IsReservedWindowsName(nameWithoutExtension);
	}

	private static bool IsReservedWindowsName(string name)
	{
		if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (name.Length == 4 &&
			(name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
			(name[3] is >= '1' and <= '9'))
		{
			return true;
		}

		return false;
	}

	private static void ThrowIfReparseEntry(ZipArchiveEntry entry)
	{
		const int UnixFileTypeMask = 0xF000;
		const int UnixSymbolicLink = 0xA000;
		int unixFileType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
		bool hasWindowsReparseAttribute =
			(entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;

		if ((unixFileType == UnixSymbolicLink) || hasWindowsReparseAttribute)
		{
			throw new InvalidDataException(
				"Update package contains a reparse-point entry.");
		}
	}

	private static async Task<long> CopyBoundedAsync(
		Stream source,
		Stream destination,
		long declaredLength,
		long remainingTotalLength,
		CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[81920];
		long copiedLength = 0;

		while (true)
		{
			int readLength = await source.ReadAsync(buffer, cancellationToken);
			if (readLength == 0)
			{
				break;
			}

			if ((copiedLength > (declaredLength - readLength)) ||
				(copiedLength > (remainingTotalLength - readLength)))
			{
				throw new InvalidDataException(
					"Update package expanded data exceeds its declared limits.");
			}

			await destination.WriteAsync(
				buffer.AsMemory(0, readLength),
				cancellationToken);
			copiedLength += readLength;
		}

		if (copiedLength != declaredLength)
		{
			throw new InvalidDataException(
				"Update package entry length does not match its ZIP metadata.");
		}

		return copiedLength;
	}

	private sealed record PlannedEntry(
		ZipArchiveEntry Entry,
		string RelativePath,
		bool IsDirectory);
}
