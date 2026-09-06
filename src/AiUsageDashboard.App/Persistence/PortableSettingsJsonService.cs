using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Win32.SafeHandles;

using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Persistence;

internal sealed record PortableSettingsSnapshot(
	IReadOnlyList<AccountProfile> Accounts,
	UsageSortMode UsageSortMode,
	UsageDisplayMode UsageDisplayMode,
	PortableWidgetPreferences? WidgetPreferences = null);

internal sealed record PortableWidgetPreferences(
	bool IsWidgetVisible,
	bool IsCollapsed,
	bool IsTopmost,
	FloatingWidgetCorner Corner,
	AppTheme? Theme = null,
	bool? IsHeightFollowingCardCount = false);

internal sealed class PortableSettingsException : Exception
{
	internal PortableSettingsException(string message)
		: base(message)
	{
	}

	internal PortableSettingsException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

internal interface IPortableSettingsFileOperations
{
	bool FileExists(string filePath);

	void DeleteFile(string filePath);

	void FlushToDisk(FileStream stream);

	void MoveFile(string sourceFilePath, string destinationFilePath);

	void ReplaceFile(
		string sourceFilePath,
		string destinationFilePath,
		string destinationBackupFilePath,
		bool ignoreMetadataErrors);
}

internal sealed class PortableSettingsFileOperations :
	IPortableSettingsFileOperations
{
	public bool FileExists(string filePath)
	{
		return File.Exists(filePath);
	}

	public void DeleteFile(string filePath)
	{
		File.Delete(filePath);
	}

	public void FlushToDisk(FileStream stream)
	{
		stream.Flush(flushToDisk: true);
	}

	public void MoveFile(string sourceFilePath, string destinationFilePath)
	{
		File.Move(sourceFilePath, destinationFilePath);
	}

	public void ReplaceFile(
		string sourceFilePath,
		string destinationFilePath,
		string destinationBackupFilePath,
		bool ignoreMetadataErrors)
	{
		File.Replace(
			sourceFilePath,
			destinationFilePath,
			destinationBackupFilePath,
			ignoreMetadataErrors);
	}
}

internal static class PortableSettingsPathIdentity
{
	[StructLayout(LayoutKind.Sequential)]
	private struct NativeFileTime
	{
		internal uint LowDateTime;
		internal uint HighDateTime;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ByHandleFileInformation
	{
		internal uint FileAttributes;
		internal NativeFileTime CreationTime;
		internal NativeFileTime LastAccessTime;
		internal NativeFileTime LastWriteTime;
		internal uint VolumeSerialNumber;
		internal uint FileSizeHigh;
		internal uint FileSizeLow;
		internal uint NumberOfLinks;
		internal uint FileIndexHigh;
		internal uint FileIndexLow;
	}

	private const uint FileFlagBackupSemantics = 0x02000000;
	private const uint FileNameNormalized = 0x00000000;
	private const uint FileNameOpened = 0x00000008;
	private const uint VolumeNameNt = 0x00000002;
	private const uint DriveRemote = 4;
	private const int ErrorAccessDenied = 5;

	internal static string ResolveForComparison(string path)
	{
		string fullPath = Path.GetFullPath(path);

		if (!OperatingSystem.IsWindows())
		{
			return fullPath;
		}

		List<string> missingSegments = [];
		string existingPath = fullPath;

		while (!PathExists(existingPath))
		{
			string trimmedPath = Path.TrimEndingDirectorySeparator(existingPath);
			string segment = Path.GetFileName(trimmedPath);
			string? parentPath = Path.GetDirectoryName(trimmedPath);

			if (string.IsNullOrWhiteSpace(segment) ||
				string.IsNullOrWhiteSpace(parentPath) ||
				string.Equals(
					parentPath,
					existingPath,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new DirectoryNotFoundException(
					"No existing ancestor could be resolved for the path.");
			}

			missingSegments.Add(segment);
			existingPath = parentPath;
		}

		string resolvedPath = GetFinalNtPath(existingPath);

		for (int index = missingSegments.Count - 1; index >= 0; index--)
		{
			resolvedPath = Path.Combine(resolvedPath, missingSegments[index]);
		}

		return resolvedPath;
	}

	internal static bool HasMultipleHardLinks(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		FileAttributes attributes;

		try
		{
			attributes = File.GetAttributes(path);
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}

		if ((attributes & FileAttributes.Directory) != 0)
		{
			return false;
		}

		using SafeFileHandle handle = OpenPathForIdentity(path);

		if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
		{
			throw CreateNativeIOException(
				"The file link count could not be verified.");
		}

		return info.NumberOfLinks > 1;
	}

	private static bool PathExists(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return true;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
	}

	private static string GetFinalNtPath(string path)
	{
		// The volume's final NT path is stable across drive letters, SUBST
		// mappings, mount points, junctions, and long/short-name aliases.
		using SafeFileHandle handle = OpenPathForIdentity(path);
		return ResolveFinalNtPath(
			IsConfirmedRemotePath(path),
			flags =>
			{
				bool succeeded = TryGetFinalNtPath(
					handle,
					flags,
					out string? resolvedPath,
					out int errorCode);
				return (succeeded, resolvedPath, errorCode);
			});
	}

	internal static string ResolveFinalNtPathForTesting(
		bool isConfirmedRemotePath,
		Func<uint, (bool Succeeded, string? ResolvedPath, int ErrorCode)> query)
	{
		return ResolveFinalNtPath(isConfirmedRemotePath, query);
	}

	private static string ResolveFinalNtPath(
		bool isConfirmedRemotePath,
		Func<uint, (bool Succeeded, string? ResolvedPath, int ErrorCode)> query)
	{
		ArgumentNullException.ThrowIfNull(query);
		(bool Succeeded, string? ResolvedPath, int ErrorCode) result = query(
			FileNameNormalized | VolumeNameNt);

		if (result.Succeeded && result.ResolvedPath is not null)
		{
			return result.ResolvedPath;
		}

		// SMB servers may deny normalized-name component queries even though
		// the user can read and write the selected share. Only confirmed remote
		// paths may fall back to the opened name; local paths remain fail-closed
		// so junction, SUBST, and short-name aliases cannot evade comparison.
		if (isConfirmedRemotePath && result.ErrorCode == ErrorAccessDenied)
		{
			result = query(FileNameOpened | VolumeNameNt);

			if (result.Succeeded && result.ResolvedPath is not null)
			{
				return result.ResolvedPath;
			}
		}

		throw CreateNativeIOException(
			"The existing path identity could not be resolved.",
			result.ErrorCode);
	}

	private static bool TryGetFinalNtPath(
		SafeFileHandle handle,
		uint flags,
		out string? resolvedPath,
		out int errorCode)
	{
		resolvedPath = null;
		errorCode = 0;
		StringBuilder buffer = new(capacity: 512);

		while (true)
		{
			uint characterCount = GetFinalPathNameByHandleW(
				handle,
				buffer,
				checked((uint)buffer.Capacity),
				flags);

			if (characterCount == 0)
			{
				errorCode = Marshal.GetLastWin32Error();
				return false;
			}

			if (characterCount < buffer.Capacity)
			{
				resolvedPath = buffer.ToString();
				return true;
			}

			buffer = new StringBuilder(checked((int)characterCount + 1));
		}
	}

	private static bool IsConfirmedRemotePath(string path)
	{
		return IsConfirmedRemotePath(path, GetDriveTypeW);
	}

	internal static bool IsConfirmedRemotePathForTesting(
		string path,
		Func<string, uint> getDriveType)
	{
		return IsConfirmedRemotePath(path, getDriveType);
	}

	private static bool IsConfirmedRemotePath(
		string path,
		Func<string, uint> getDriveType)
	{
		ArgumentNullException.ThrowIfNull(getDriveType);
		string fullPath = Path.GetFullPath(path);
		string? rootPath = Path.GetPathRoot(fullPath);

		if (string.IsNullOrWhiteSpace(rootPath))
		{
			return false;
		}

		if (rootPath.StartsWith(@"\\", StringComparison.Ordinal))
		{
			return true;
		}

		string driveRoot = Path.EndsInDirectorySeparator(rootPath)
			? rootPath
			: rootPath + Path.DirectorySeparatorChar;
		return getDriveType(driveRoot) == DriveRemote;
	}

	private static SafeFileHandle OpenPathForIdentity(string path)
	{
		SafeFileHandle handle = CreateFileW(
			path,
			desiredAccess: 0,
			FileShare.ReadWrite | FileShare.Delete,
			IntPtr.Zero,
			FileMode.Open,
			FileFlagBackupSemantics,
			IntPtr.Zero);

		if (handle.IsInvalid)
		{
			int errorCode = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new IOException(
				"The existing path could not be opened for identity verification.",
				new Win32Exception(errorCode));
		}

		return handle;
	}

	private static IOException CreateNativeIOException(string message)
	{
		return CreateNativeIOException(message, Marshal.GetLastWin32Error());
	}

	private static IOException CreateNativeIOException(
		string message,
		int errorCode)
	{
		return new IOException(
			message,
			new Win32Exception(errorCode));
	}

	[DllImport(
		"kernel32.dll",
		CharSet = CharSet.Unicode,
		EntryPoint = "CreateFileW",
		ExactSpelling = true,
		SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(
		string fileName,
		uint desiredAccess,
		FileShare shareMode,
		IntPtr securityAttributes,
		FileMode creationDisposition,
		uint flagsAndAttributes,
		IntPtr templateFile);

	[DllImport(
		"kernel32.dll",
		CharSet = CharSet.Unicode,
		EntryPoint = "GetFinalPathNameByHandleW",
		ExactSpelling = true,
		SetLastError = true)]
	private static extern uint GetFinalPathNameByHandleW(
		SafeFileHandle file,
		StringBuilder filePath,
		uint filePathLength,
		uint flags);

	[DllImport(
		"kernel32.dll",
		CharSet = CharSet.Unicode,
		EntryPoint = "GetDriveTypeW",
		ExactSpelling = true)]
	private static extern uint GetDriveTypeW(string rootPathName);

	[DllImport(
		"kernel32.dll",
		EntryPoint = "GetFileInformationByHandle",
		ExactSpelling = true,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetFileInformationByHandle(
		SafeFileHandle file,
		out ByHandleFileInformation fileInformation);
}

internal sealed class PortableSettingsJsonService
{
	private sealed record AccountDocument(
		Guid Id,
		ProviderKind Provider,
		string DisplayName,
		bool IsEnabled,
		string? ProviderAccountIdentity,
		bool ShowSubscriptionContext);

	private sealed record PreferencesDocument(
		UsageSortMode UsageSortMode,
		UsageDisplayMode UsageDisplayMode,
		bool IsWidgetVisible,
		bool IsCollapsed,
		bool IsTopmost,
		FloatingWidgetCorner Corner,
		AppTheme Theme,
		bool IsHeightFollowingCardCount);

	private sealed record SettingsDocument(
		string Format,
		int SchemaVersion,
		IReadOnlyList<AccountDocument> Accounts,
		PreferencesDocument Preferences);

	private const int CurrentSchemaVersion = 6;
	private const int HeightFollowingCardCountSchemaVersion = 6;
	private const int LegacySchemaVersionOne = 1;
	private const int LegacySchemaVersionTwo = 2;
	private const int LegacySchemaVersionThree = 3;
	private const int LegacySchemaVersionFour = 4;
	private const int LegacySchemaVersionFive = 5;
	private const int MaximumAccountCount = 256;
	private const int MaximumDepth = 16;
	private const int MaximumDisplayNameLength = 80;
	private const int MaximumDocumentSizeBytes = 1024 * 1024;
	private const string PortableFormat = "ai-usage-dashboard-settings";
	private const int SubscriptionContextSchemaVersion = 5;
	private const int ThemeSchemaVersion = 4;
	private static readonly string[] CurrentAccountPropertyNames =
	[
		"id",
		"provider",
		"displayName",
		"isEnabled",
		"providerAccountIdentity",
		"showSubscriptionContext"
	];
	private static readonly string[] LegacyAccountPropertyNames =
	[
		"id",
		"provider",
		"displayName",
		"isEnabled",
		"providerAccountIdentity"
	];
	private static readonly string[] CurrentPreferencesPropertyNames =
	[
		"usageSortMode",
		"usageDisplayMode",
		"isWidgetVisible",
		"isCollapsed",
		"isTopmost",
		"corner",
		"theme",
		"isHeightFollowingCardCount"
	];
	private static readonly string[] SchemaFourAndFivePreferencesPropertyNames =
	[
		"usageSortMode",
		"usageDisplayMode",
		"isWidgetVisible",
		"isCollapsed",
		"isTopmost",
		"corner",
		"theme"
	];
	private static readonly string[] SchemaThreePreferencesPropertyNames =
	[
		"usageSortMode",
		"usageDisplayMode",
		"isWidgetVisible",
		"isCollapsed",
		"isTopmost",
		"corner"
	];
	private static readonly string[] LegacyPreferencesPropertyNames =
	[
		"usageSortMode",
		"usageDisplayMode"
	];
	private static readonly string[] RootPropertyNames =
	[
		"format",
		"schemaVersion",
		"accounts",
		"preferences"
	];
	private static readonly JsonSerializerOptions SerializerOptions =
		CreateSerializerOptions();
	private readonly string _appManagedDataDirectoryPath;
	private readonly IPortableSettingsFileOperations _fileOperations;

	internal PortableSettingsJsonService()
		: this(
			new PortableSettingsFileOperations(),
			GetAppManagedDataDirectoryPath())
	{
	}

	internal PortableSettingsJsonService(
		IPortableSettingsFileOperations fileOperations)
		: this(fileOperations, GetAppManagedDataDirectoryPath())
	{
	}

	internal PortableSettingsJsonService(
		IPortableSettingsFileOperations fileOperations,
		string appManagedDataDirectoryPath)
	{
		_fileOperations = fileOperations ??
			throw new ArgumentNullException(nameof(fileOperations));
		ArgumentException.ThrowIfNullOrWhiteSpace(appManagedDataDirectoryPath);
		_appManagedDataDirectoryPath = Path.GetFullPath(
			appManagedDataDirectoryPath);
	}

	internal async Task ExportAsync(
		string filePath,
		PortableSettingsSnapshot snapshot,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			throw new PortableSettingsException("匯出檔案路徑不可為空。");
		}

		if (snapshot is null)
		{
			throw new PortableSettingsException("沒有可匯出的設定。");
		}

		SettingsDocument document = CreateDocument(snapshot);
		await Task.Run(
			() => ExportCoreAsync(filePath, document, cancellationToken),
			cancellationToken).ConfigureAwait(false);
	}

	private async Task ExportCoreAsync(
		string filePath,
		SettingsDocument document,
		CancellationToken cancellationToken)
	{
		string? temporaryFilePath = null;

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			EnsureDestinationDoesNotUseWindowsDeviceNamespace(filePath);
			string destinationPath = Path.GetFullPath(filePath);
			EnsureDestinationDoesNotTraverseReparsePoint(destinationPath);
			EnsureDestinationIsNotHardLinked(destinationPath);
			EnsureDestinationIsOutsideAppManagedDataDirectory(destinationPath);
			string? directoryPath = Path.GetDirectoryName(destinationPath);

			if (string.IsNullOrWhiteSpace(directoryPath))
			{
				throw new PortableSettingsException("匯出檔案缺少有效的所在資料夾。");
			}

			Directory.CreateDirectory(directoryPath);
			// Recheck after creating missing parents. This closes both ordinary
			// and reverse junction-alias paths before a temporary file is written.
			EnsureDestinationDoesNotTraverseReparsePoint(destinationPath);
			EnsureDestinationIsNotHardLinked(destinationPath);
			EnsureDestinationIsOutsideAppManagedDataDirectory(destinationPath);
			temporaryFilePath = Path.Combine(
				directoryPath,
				$".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

			await using (FileStream stream = new(
				temporaryFilePath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await JsonSerializer.SerializeAsync(
					stream,
					document,
					SerializerOptions,
					cancellationToken).ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				_fileOperations.FlushToDisk(stream);
			}

			cancellationToken.ThrowIfCancellationRequested();
			EnsureDestinationDoesNotTraverseReparsePoint(destinationPath);
			EnsureDestinationIsNotHardLinked(destinationPath);
			EnsureDestinationIsOutsideAppManagedDataDirectory(destinationPath);
			string committedTemporaryFilePath = temporaryFilePath;
			// CommitTemporaryFile owns every post-commit copy. On a partial
			// replacement failure it deliberately retains recovery artifacts.
			temporaryFilePath = null;
			CommitTemporaryFile(committedTemporaryFilePath, destinationPath);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (PortableSettingsException)
		{
			throw;
		}
		catch (Exception exception) when (
			(exception is IOException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is NotSupportedException) ||
			(exception is ArgumentException))
		{
			throw new PortableSettingsException(
				"設定匯出失敗；目的檔案可能已變更。請先保留同一資料夾內的暫存檔或備份，以便還原。",
				exception);
		}
		finally
		{
			DeleteFileBestEffort(temporaryFilePath);
		}
	}

	private static void EnsureDestinationDoesNotUseWindowsDeviceNamespace(
		string destinationPath)
	{
		if (!OperatingSystem.IsWindows() ||
			destinationPath.Length < 4)
		{
			return;
		}

		bool hasDevicePrefix =
			IsDirectorySeparator(destinationPath[0]) &&
			((IsDirectorySeparator(destinationPath[1]) &&
				((destinationPath[2] == '?') ||
					(destinationPath[2] == '.')) &&
				IsDirectorySeparator(destinationPath[3])) ||
				((destinationPath[1] == '?') &&
					(destinationPath[2] == '?') &&
					IsDirectorySeparator(destinationPath[3])));

		if (hasDevicePrefix)
		{
			throw new PortableSettingsException(
				"不可使用 Windows 裝置命名空間匯出設定；請透過一般磁碟或網路路徑選擇目的檔案。");
		}
	}

	private static bool IsDirectorySeparator(char character)
	{
		return (character == Path.DirectorySeparatorChar) ||
			(character == Path.AltDirectorySeparatorChar);
	}

	private static void EnsureDestinationDoesNotTraverseReparsePoint(
		string destinationPath)
	{
		string? currentPath = Path.GetFullPath(destinationPath);

		while (!string.IsNullOrWhiteSpace(currentPath))
		{
			try
			{
				FileAttributes attributes = File.GetAttributes(currentPath);

				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					throw new PortableSettingsException(
						"不可透過連結的資料夾匯出設定。請選擇一般資料夾，以免覆寫 AI Usage 管理的資料。");
				}
			}
			catch (FileNotFoundException)
			{
				// Missing destination components are checked again after creation.
			}
			catch (DirectoryNotFoundException)
			{
				// Continue upward until reaching the first existing parent.
			}

			string? parentPath = Path.GetDirectoryName(
				currentPath.TrimEnd(
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar));

			if (string.IsNullOrWhiteSpace(parentPath) ||
				string.Equals(
					parentPath,
					currentPath,
					StringComparison.OrdinalIgnoreCase))
			{
				break;
			}

			currentPath = parentPath;
		}
	}

	private static void EnsureDestinationIsNotHardLinked(string destinationPath)
	{
		if (!PortableSettingsPathIdentity.HasMultipleHardLinks(destinationPath))
		{
			return;
		}

		throw new PortableSettingsException(
			"不可覆寫硬連結檔案來匯出設定；請選擇一般檔案，以免透過別名覆寫程式管理的資料。");
	}

	private void EnsureDestinationIsOutsideAppManagedDataDirectory(
		string destinationPath)
	{
		bool isManagedDestination = IsSamePathOrDescendant(
			destinationPath,
			_appManagedDataDirectoryPath);

		if (!isManagedDestination && OperatingSystem.IsWindows())
		{
			string resolvedDestinationPath =
				PortableSettingsPathIdentity.ResolveForComparison(destinationPath);
			string resolvedManagedDataDirectoryPath =
				PortableSettingsPathIdentity.ResolveForComparison(
					_appManagedDataDirectoryPath);
			isManagedDestination = IsSameNormalizedPathOrDescendant(
				resolvedDestinationPath,
				resolvedManagedDataDirectoryPath);
		}

		if (!isManagedDestination)
		{
			return;
		}

		throw new PortableSettingsException(
			"不可將設定匯出到 AI Usage 管理的資料夾。請選擇其他位置，以免覆寫目前設定或還原資料。");
	}

	private static bool IsSamePathOrDescendant(
		string candidatePath,
		string directoryPath)
	{
		string normalizedCandidatePath = Path.GetFullPath(candidatePath)
			.TrimEnd(
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar);
		string normalizedDirectoryPath = Path.GetFullPath(directoryPath)
			.TrimEnd(
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar);
		return IsSameNormalizedPathOrDescendant(
			normalizedCandidatePath,
			normalizedDirectoryPath);
	}

	private static bool IsSameNormalizedPathOrDescendant(
		string candidatePath,
		string directoryPath)
	{
		string normalizedCandidatePath = candidatePath.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		string normalizedDirectoryPath = directoryPath.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);

		if (string.Equals(
				normalizedCandidatePath,
				normalizedDirectoryPath,
				StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return normalizedCandidatePath.StartsWith(
			normalizedDirectoryPath + Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase) ||
			((Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar) &&
				normalizedCandidatePath.StartsWith(
					normalizedDirectoryPath + Path.AltDirectorySeparatorChar,
					StringComparison.OrdinalIgnoreCase));
	}

	private static string GetAppManagedDataDirectoryPath()
	{
		string? directoryPath = Path.GetDirectoryName(
			AppDataPaths.GetAccountSettingsFilePath());

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new InvalidOperationException(
				"無法取得 AI Usage 管理的資料夾。");
		}

		return directoryPath;
	}

	internal async Task<PortableSettingsSnapshot> ImportAsync(
		string filePath,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			throw new PortableSettingsException("匯入檔案路徑不可為空。");
		}

		try
		{
			return await Task.Run(
				async () =>
				{
					byte[] contents = await ReadDocumentAsync(
						Path.GetFullPath(filePath),
						cancellationToken).ConfigureAwait(false);
					return ParseDocument(contents);
				},
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (PortableSettingsException)
		{
			throw;
		}
		catch (Exception exception) when (
			(exception is IOException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is NotSupportedException) ||
			(exception is ArgumentException) ||
			(exception is JsonException) ||
			(exception is OverflowException))
		{
			throw new PortableSettingsException(
				"設定匯入失敗；請確認檔案格式與存取權限。",
				exception);
		}
	}

	private void CommitTemporaryFile(
		string temporaryFilePath,
		string destinationPath)
	{
		if (!_fileOperations.FileExists(destinationPath))
		{
			_fileOperations.MoveFile(temporaryFilePath, destinationPath);
			return;
		}

		string backupFilePath = CreateUniqueBackupFilePath(destinationPath);

		try
		{
			_fileOperations.ReplaceFile(
				temporaryFilePath,
				destinationPath,
				backupFilePath,
				ignoreMetadataErrors: false);
		}
		catch (Exception exception) when (IsFileOperationException(exception))
		{
			RecoverAfterFailedReplacement(
				temporaryFilePath,
				destinationPath,
				backupFilePath);
			throw new PortableSettingsException(
				"設定檔覆寫未完成；目的檔案可能已變更。請先保留同一資料夾內的暫存檔或備份，以便還原。",
				exception);
		}

		DeleteFileBestEffort(backupFilePath);
	}

	private string CreateUniqueBackupFilePath(string destinationPath)
	{
		string? directoryPath = Path.GetDirectoryName(destinationPath);
		string destinationFileName = Path.GetFileName(destinationPath);

		if (string.IsNullOrWhiteSpace(directoryPath))
		{
			throw new PortableSettingsException("匯出檔案缺少有效的所在資料夾。");
		}

		while (true)
		{
			string candidatePath = Path.Combine(
				directoryPath,
				$".{destinationFileName}.{Guid.NewGuid():N}.bak");

			if (!_fileOperations.FileExists(candidatePath))
			{
				return candidatePath;
			}
		}
	}

	private void RecoverAfterFailedReplacement(
		string temporaryFilePath,
		string destinationPath,
		string backupFilePath)
	{
		if (FileExistsBestEffort(destinationPath))
		{
			return;
		}

		TryRestoreFile(backupFilePath, destinationPath);

		if (!FileExistsBestEffort(destinationPath))
		{
			TryRestoreFile(temporaryFilePath, destinationPath);
		}
	}

	private void TryRestoreFile(
		string recoveryFilePath,
		string destinationPath)
	{
		if (!FileExistsBestEffort(recoveryFilePath))
		{
			return;
		}

		try
		{
			_fileOperations.MoveFile(recoveryFilePath, destinationPath);
		}
		catch (Exception exception) when (IsFileOperationException(exception))
		{
			// 保留原路徑的副本；呼叫端會提示使用者不要刪除還原檔。
		}
	}

	private bool FileExistsBestEffort(string filePath)
	{
		try
		{
			return _fileOperations.FileExists(filePath);
		}
		catch (Exception exception) when (IsFileOperationException(exception))
		{
			return false;
		}
	}

	private static SettingsDocument CreateDocument(
		PortableSettingsSnapshot snapshot)
	{
		if (!Enum.IsDefined(snapshot.UsageSortMode))
		{
			throw new PortableSettingsException("用量排序方式無效。");
		}

		if (!Enum.IsDefined(snapshot.UsageDisplayMode))
		{
			throw new PortableSettingsException("用量顯示方式無效。");
		}

		if (snapshot.WidgetPreferences is null)
		{
			throw new PortableSettingsException("缺少浮窗設定。");
		}

		if (!Enum.IsDefined(snapshot.WidgetPreferences.Corner))
		{
			throw new PortableSettingsException("浮窗停靠位置無效。");
		}

		if ((snapshot.WidgetPreferences.Theme is not AppTheme theme) ||
			!Enum.IsDefined(theme))
		{
			throw new PortableSettingsException("主題設定無效。");
		}

		if (snapshot.WidgetPreferences.IsHeightFollowingCardCount is null)
		{
			throw new PortableSettingsException("缺少視窗高度設定。");
		}

		if (snapshot.Accounts is null)
		{
			throw new PortableSettingsException("帳號清單不可為空值。");
		}

		if (snapshot.Accounts.Count > MaximumAccountCount)
		{
			throw new PortableSettingsException(
				$"帳號數量不可超過 {MaximumAccountCount} 個。");
		}

		HashSet<Guid> accountIds = [];
		Dictionary<ProviderKind, HashSet<string>> providerIdentities = [];
		List<AccountDocument> accountDocuments = new(snapshot.Accounts.Count);

		foreach (AccountProfile? account in snapshot.Accounts)
		{
			if (account is null)
			{
				throw new PortableSettingsException("帳號項目不可為空值。");
			}

			ValidateAccountId(account.Id, accountIds);
			ValidateProvider(account.Provider);
			string displayName = NormalizeDisplayName(account.DisplayName);
			// Claude、Codex、Copilot、AGY 與 Grok binding 只代表本機已核准的登入環境，
			// 不能當成可攜的 provider account identity。
			string? providerIdentity = IsMachineLocalIdentityProvider(
				account.Provider)
				? null
				: NormalizeProviderIdentity(account.ProviderAccountIdentity);
			EnsureUniqueProviderIdentity(
				providerIdentities,
				account.Provider,
				providerIdentity);
			accountDocuments.Add(new AccountDocument(
				account.Id,
				account.Provider,
				displayName,
				account.IsEnabled,
				providerIdentity,
				account.ShowSubscriptionContext));
		}

		return new SettingsDocument(
			PortableFormat,
			CurrentSchemaVersion,
			accountDocuments,
			new PreferencesDocument(
				snapshot.UsageSortMode,
				snapshot.UsageDisplayMode,
				snapshot.WidgetPreferences.IsWidgetVisible,
				snapshot.WidgetPreferences.IsCollapsed,
				snapshot.WidgetPreferences.IsTopmost,
				snapshot.WidgetPreferences.Corner,
				theme,
				snapshot.WidgetPreferences.IsHeightFollowingCardCount.Value));
	}

	private static JsonSerializerOptions CreateSerializerOptions()
	{
		JsonSerializerOptions options = new()
		{
			MaxDepth = MaximumDepth,
			PropertyNameCaseInsensitive = false,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			WriteIndented = true
		};
		options.Converters.Add(new JsonStringEnumConverter(
			namingPolicy: null,
			allowIntegerValues: false));
		return options;
	}

	private void DeleteFileBestEffort(string? filePath)
	{
		if (filePath is null)
		{
			return;
		}

		try
		{
			if (_fileOperations.FileExists(filePath))
			{
				_fileOperations.DeleteFile(filePath);
			}
		}
		catch (Exception exception) when (IsFileOperationException(exception))
		{
			// 唯一命名的暫存／備份檔不會被自動匯入；清理採最佳努力。
		}
	}

	private static bool IsFileOperationException(Exception exception)
	{
		return (exception is IOException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is NotSupportedException) ||
			(exception is ArgumentException);
	}

	private static void EnsureUniqueProviderIdentity(
		Dictionary<ProviderKind, HashSet<string>> providerIdentities,
		ProviderKind provider,
		string? providerIdentity)
	{
		if (providerIdentity is null)
		{
			return;
		}

		if (!providerIdentities.TryGetValue(
				provider,
				out HashSet<string>? identities))
		{
			identities = new HashSet<string>(
				ProviderAccountIdentityRules.Comparer);
			providerIdentities.Add(provider, identities);
		}

		if (!identities.Add(providerIdentity))
		{
			throw new PortableSettingsException(
				"同一服務的帳號不可重複。");
		}
	}

	private static string NormalizeDisplayName(string? displayName)
	{
		if (displayName is null)
		{
			throw new PortableSettingsException("帳號顯示名稱不可為空值。");
		}

		string normalizedDisplayName = displayName.Trim();

		if (normalizedDisplayName.Length > MaximumDisplayNameLength)
		{
			throw new PortableSettingsException(
				$"帳號顯示名稱不可超過 {MaximumDisplayNameLength} 個字元。");
		}

		if (normalizedDisplayName.Any(char.IsControl))
		{
			throw new PortableSettingsException(
				"帳號顯示名稱不可包含控制字元。");
		}

		return normalizedDisplayName;
	}

	private static string? NormalizeProviderIdentity(string? providerIdentity)
	{
		if (ProviderAccountIdentityRules.TryNormalize(
				providerIdentity,
				out string? normalizedIdentity))
		{
			return normalizedIdentity;
		}

		throw new PortableSettingsException("帳號資訊格式無效。");
	}

	private static PortableSettingsSnapshot ParseDocument(byte[] contents)
	{
		JsonDocumentOptions options = new()
		{
			AllowTrailingCommas = false,
			CommentHandling = JsonCommentHandling.Disallow,
			MaxDepth = MaximumDepth
		};

		try
		{
			using JsonDocument document = JsonDocument.Parse(contents, options);
			JsonElement root = document.RootElement;
			int schemaVersion = ReadSchemaVersionForRouting(root);

			if (schemaVersion > CurrentSchemaVersion)
			{
				throw new PortableSettingsException(
					"設定檔由較新版本建立，目前無法匯入。");
			}

			if (schemaVersion is not CurrentSchemaVersion and
				not LegacySchemaVersionOne and
				not LegacySchemaVersionTwo and
				not LegacySchemaVersionThree and
				not LegacySchemaVersionFour and
				not LegacySchemaVersionFive)
			{
				throw new PortableSettingsException("設定檔版本不受支援。");
			}

			ValidateObject(root, RootPropertyNames, "設定檔根節點格式無效。");

			IReadOnlyList<AccountProfile> accounts = ReadAccounts(
				root.GetProperty("accounts"),
				schemaVersion);
			(
				UsageSortMode sortMode,
				UsageDisplayMode displayMode,
				PortableWidgetPreferences? widgetPreferences) = ReadPreferences(
				root.GetProperty("preferences"),
				schemaVersion);
			return new PortableSettingsSnapshot(
				accounts,
				sortMode,
				displayMode,
				widgetPreferences);
		}
		catch (JsonException exception)
		{
			throw new PortableSettingsException(
				"設定檔不是有效且受支援的 JSON。",
				exception);
		}
	}

	private static int ReadSchemaVersionForRouting(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new PortableSettingsException("設定檔根節點格式無效。");
		}

		JsonElement formatElement = default;
		JsonElement schemaVersionElement = default;
		bool hasFormat = false;
		bool hasSchemaVersion = false;

		foreach (JsonProperty property in root.EnumerateObject())
		{
			if (string.Equals(property.Name, "format", StringComparison.Ordinal))
			{
				if (hasFormat)
				{
					throw new PortableSettingsException(
						"設定檔包含未知或重複的欄位。");
				}

				hasFormat = true;
				formatElement = property.Value;
			}
			else if (string.Equals(
				property.Name,
				"schemaVersion",
				StringComparison.Ordinal))
			{
				if (hasSchemaVersion)
				{
					throw new PortableSettingsException(
						"設定檔包含未知或重複的欄位。");
				}

				hasSchemaVersion = true;
				schemaVersionElement = property.Value;
			}
		}

		if (!hasFormat ||
			(formatElement.ValueKind != JsonValueKind.String) ||
			!string.Equals(
				formatElement.GetString(),
				PortableFormat,
				StringComparison.Ordinal))
		{
			throw new PortableSettingsException("設定檔格式識別無效。");
		}

		if (!hasSchemaVersion ||
			(schemaVersionElement.ValueKind != JsonValueKind.Number) ||
			!schemaVersionElement.TryGetInt32(out int schemaVersion))
		{
			throw new PortableSettingsException("設定檔版本格式無效。");
		}

		return schemaVersion;
	}

	private static async Task<byte[]> ReadDocumentAsync(
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

		if (stream.Length > MaximumDocumentSizeBytes)
		{
			throw new PortableSettingsException(
				"設定檔不可超過 1 MiB。");
		}

		using MemoryStream contents = new(
			capacity: checked((int)stream.Length));
		byte[] buffer = new byte[81920];

		while (true)
		{
			int bytesRead = await stream.ReadAsync(
				buffer,
				cancellationToken).ConfigureAwait(false);

			if (bytesRead == 0)
			{
				break;
			}

			if ((contents.Length + bytesRead) > MaximumDocumentSizeBytes)
			{
				throw new PortableSettingsException(
					"設定檔不可超過 1 MiB。");
			}

			contents.Write(buffer, 0, bytesRead);
		}

		return contents.ToArray();
	}

	private static IReadOnlyList<AccountProfile> ReadAccounts(
		JsonElement accountsElement,
		int schemaVersion)
	{
		if ((accountsElement.ValueKind != JsonValueKind.Array) ||
			(accountsElement.GetArrayLength() > MaximumAccountCount))
		{
			throw new PortableSettingsException("帳號清單格式或數量無效。");
		}

		HashSet<Guid> accountIds = [];
		Dictionary<ProviderKind, HashSet<string>> providerIdentities = [];
		List<AccountProfile> accounts = new(accountsElement.GetArrayLength());

		foreach (JsonElement accountElement in accountsElement.EnumerateArray())
		{
			ValidateObject(
				accountElement,
				schemaVersion >= SubscriptionContextSchemaVersion
					? CurrentAccountPropertyNames
					: LegacyAccountPropertyNames,
				"帳號項目格式無效。");
			Guid id = ReadAccountId(accountElement.GetProperty("id"));
			ValidateAccountId(id, accountIds);
			ProviderKind provider = ReadProvider(
				accountElement.GetProperty("provider"));
			string displayName = ReadDisplayName(
				accountElement.GetProperty("displayName"));
			bool isEnabled = ReadBoolean(
				accountElement.GetProperty("isEnabled"),
				"帳號啟用狀態格式無效。");
			bool showSubscriptionContext =
				schemaVersion >= SubscriptionContextSchemaVersion &&
				ReadBoolean(
					accountElement.GetProperty("showSubscriptionContext"),
					"卡片顯示設定格式無效。");
			string? serializedProviderIdentity = ReadProviderIdentity(
				accountElement.GetProperty("providerAccountIdentity"));
			string? providerIdentity = IsMachineLocalIdentityProvider(provider)
				? null
				: serializedProviderIdentity;
			EnsureUniqueProviderIdentity(
				providerIdentities,
				provider,
				providerIdentity);
			accounts.Add(new AccountProfile(
				id,
				provider,
				displayName,
				isEnabled,
				providerIdentity,
				HasAcceptedClaudeQuotaRisk: false,
				ShowSubscriptionContext: showSubscriptionContext));
		}

		return accounts.ToArray();
	}

	private static Guid ReadAccountId(JsonElement idElement)
	{
		if ((idElement.ValueKind != JsonValueKind.String) ||
			!Guid.TryParse(idElement.GetString(), out Guid id))
		{
			throw new PortableSettingsException("帳號資料格式無效。");
		}

		return id;
	}

	private static bool ReadBoolean(
		JsonElement element,
		string invalidMessage)
	{
		if (element.ValueKind is not (
			JsonValueKind.True or JsonValueKind.False))
		{
			throw new PortableSettingsException(invalidMessage);
		}

		return element.GetBoolean();
	}

	private static string ReadDisplayName(JsonElement displayNameElement)
	{
		if (displayNameElement.ValueKind != JsonValueKind.String)
		{
			throw new PortableSettingsException(
				"帳號顯示名稱格式無效。");
		}

		return NormalizeDisplayName(displayNameElement.GetString());
	}

	private static (
		UsageSortMode SortMode,
		UsageDisplayMode DisplayMode,
		PortableWidgetPreferences? WidgetPreferences) ReadPreferences(
		JsonElement preferencesElement,
		int schemaVersion)
	{
		IReadOnlyList<string> propertyNames = schemaVersion switch
		{
			>= HeightFollowingCardCountSchemaVersion =>
				CurrentPreferencesPropertyNames,
			>= ThemeSchemaVersion => SchemaFourAndFivePreferencesPropertyNames,
			LegacySchemaVersionThree => SchemaThreePreferencesPropertyNames,
			_ => LegacyPreferencesPropertyNames
		};
		ValidateObject(
			preferencesElement,
			propertyNames,
			"顯示設定格式無效。");
		UsageSortMode sortMode = ReadUsageSortMode(
			preferencesElement.GetProperty("usageSortMode"));
		UsageDisplayMode displayMode = ReadUsageDisplayMode(
			preferencesElement.GetProperty("usageDisplayMode"));

		if (schemaVersion is LegacySchemaVersionOne or LegacySchemaVersionTwo)
		{
			return (sortMode, displayMode, null);
		}

		PortableWidgetPreferences widgetPreferences = new(
			ReadBoolean(
				preferencesElement.GetProperty("isWidgetVisible"),
				"浮窗顯示狀態格式無效。"),
			ReadBoolean(
				preferencesElement.GetProperty("isCollapsed"),
				"浮窗收合狀態格式無效。"),
			ReadBoolean(
				preferencesElement.GetProperty("isTopmost"),
				"浮窗置頂狀態格式無效。"),
			ReadFloatingWidgetCorner(
				preferencesElement.GetProperty("corner")),
			schemaVersion >= ThemeSchemaVersion
				? ReadAppTheme(preferencesElement.GetProperty("theme"))
				: null,
			schemaVersion >= HeightFollowingCardCountSchemaVersion
				? ReadBoolean(
					preferencesElement.GetProperty(
						"isHeightFollowingCardCount"),
					"視窗高度設定格式無效。")
				: null);
		return (sortMode, displayMode, widgetPreferences);
	}

	private static AppTheme ReadAppTheme(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.String)
		{
			throw new PortableSettingsException("主題設定格式無效。");
		}

		return element.GetString() switch
		{
			nameof(AppTheme.ClassicBlue) => AppTheme.ClassicBlue,
			nameof(AppTheme.Midnight) => AppTheme.Midnight,
			nameof(AppTheme.Light) => AppTheme.Light,
			nameof(AppTheme.Sakura) => AppTheme.Sakura,
			_ => throw new PortableSettingsException("主題設定無效。")
		};
	}

	private static FloatingWidgetCorner ReadFloatingWidgetCorner(
		JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.String)
		{
			throw new PortableSettingsException("浮窗停靠位置格式無效。");
		}

		return element.GetString() switch
		{
			nameof(FloatingWidgetCorner.TopLeft) => FloatingWidgetCorner.TopLeft,
			nameof(FloatingWidgetCorner.TopRight) => FloatingWidgetCorner.TopRight,
			nameof(FloatingWidgetCorner.BottomRight) =>
				FloatingWidgetCorner.BottomRight,
			nameof(FloatingWidgetCorner.BottomLeft) =>
				FloatingWidgetCorner.BottomLeft,
			_ => throw new PortableSettingsException("浮窗停靠位置無效。")
		};
	}

	private static ProviderKind ReadProvider(JsonElement providerElement)
	{
		if (providerElement.ValueKind != JsonValueKind.String)
		{
			throw new PortableSettingsException("帳號的服務格式無效。");
		}

		ProviderKind provider = providerElement.GetString() switch
		{
			nameof(ProviderKind.Claude) => ProviderKind.Claude,
			nameof(ProviderKind.Codex) => ProviderKind.Codex,
			nameof(ProviderKind.Copilot) => ProviderKind.Copilot,
			nameof(ProviderKind.Antigravity) => ProviderKind.Antigravity,
			nameof(ProviderKind.Grok) => ProviderKind.Grok,
			_ => throw new PortableSettingsException(
				"帳號包含不支援的服務。")
		};
		ValidateProvider(provider);
		return provider;
	}

	private static string? ReadProviderIdentity(JsonElement identityElement)
	{
		if (identityElement.ValueKind == JsonValueKind.Null)
		{
			return null;
		}

		if (identityElement.ValueKind != JsonValueKind.String)
		{
			throw new PortableSettingsException(
				"帳號資訊格式無效。");
		}

		return NormalizeProviderIdentity(identityElement.GetString());
	}

	private static UsageDisplayMode ReadUsageDisplayMode(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.String)
		{
			throw new PortableSettingsException("用量顯示方式格式無效。");
		}

		return element.GetString() switch
		{
			nameof(UsageDisplayMode.Used) => UsageDisplayMode.Used,
			nameof(UsageDisplayMode.Remaining) => UsageDisplayMode.Remaining,
			_ => throw new PortableSettingsException("用量顯示方式無效。")
		};
	}

	private static UsageSortMode ReadUsageSortMode(JsonElement element)
	{
		if (element.ValueKind != JsonValueKind.String)
		{
			throw new PortableSettingsException("用量排序方式格式無效。");
		}

		return element.GetString() switch
		{
			nameof(UsageSortMode.Manual) => UsageSortMode.Manual,
			nameof(UsageSortMode.Automatic) => UsageSortMode.Automatic,
			_ => throw new PortableSettingsException("用量排序方式無效。")
		};
	}

	private static void ValidateAccountId(Guid id, HashSet<Guid> accountIds)
	{
		if (id == Guid.Empty)
		{
			throw new PortableSettingsException("帳號資料不可為空。");
		}

		if (!accountIds.Add(id))
		{
			throw new PortableSettingsException("帳號不可重複。");
		}
	}

	private static void ValidateObject(
		JsonElement element,
		IReadOnlyList<string> propertyNames,
		string invalidMessage)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new PortableSettingsException(invalidMessage);
		}

		HashSet<string> expectedNames = new(
			propertyNames,
			StringComparer.Ordinal);
		HashSet<string> seenNames = new(StringComparer.Ordinal);

		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!expectedNames.Contains(property.Name) ||
				!seenNames.Add(property.Name))
			{
				throw new PortableSettingsException(
					"設定檔包含未知或重複的欄位。");
			}
		}

		if ((seenNames.Count != expectedNames.Count) ||
			expectedNames.Any(name => !seenNames.Contains(name)))
		{
			throw new PortableSettingsException(invalidMessage);
		}
	}

	private static void ValidateProvider(ProviderKind provider)
	{
		if (!Enum.IsDefined(provider))
		{
			throw new PortableSettingsException(
				"帳號包含不支援的服務。");
		}
	}

	private static bool IsMachineLocalIdentityProvider(ProviderKind provider)
	{
		return provider is ProviderKind.Claude or
			ProviderKind.Codex or
			ProviderKind.Copilot or
			ProviderKind.Antigravity or
			ProviderKind.Grok;
	}

}
