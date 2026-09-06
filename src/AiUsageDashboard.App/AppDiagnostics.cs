using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;

namespace AiUsageDashboard.App;

internal readonly record struct AppDiagnosticWriteResult(
	string? FilePath,
	bool WasWritten);

internal static class AppDiagnostics
{
	private const string ApplicationDirectoryName = "AiUsageDashboard";
	private const string DiagnosticFileName = "diagnostics.log";
	private const string DiagnosticWriteLockFileSuffix = ".lock";
	private const long MaximumDiagnosticFileBytes = 512 * 1024;
	private static readonly TimeSpan DiagnosticWriteLockRetryDelay =
		TimeSpan.FromMilliseconds(10);
	private static readonly TimeSpan DiagnosticWriteLockWaitTimeout =
		TimeSpan.FromMilliseconds(250);

	internal static string GetDiagnosticFilePath()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);

		if (string.IsNullOrWhiteSpace(localApplicationData))
		{
			throw new InvalidOperationException(
				"The current user's local application data directory is unavailable.");
		}

		return Path.Combine(
			localApplicationData,
			ApplicationDirectoryName,
			DiagnosticFileName);
	}

	internal static string GetDiagnosticWriteLockFilePath(
		string diagnosticFilePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticFilePath);
		return Path.GetFullPath(diagnosticFilePath) +
			DiagnosticWriteLockFileSuffix;
	}

	internal static string GetUserFacingFailureReason(
		Exception exception,
		string unexpectedReason = "啟動程序發生錯誤。")
	{
		ArgumentNullException.ThrowIfNull(exception);
		ArgumentException.ThrowIfNullOrWhiteSpace(unexpectedReason);

		for (Exception? current = exception;
			current is not null;
			current = current.InnerException)
		{
			switch (current)
			{
				case UnauthorizedAccessException:
				case SecurityException:
					return "Windows 不允許存取 AI Usage 的本機資料。";
				case DirectoryNotFoundException:
					return "AI Usage 的本機資料路徑不存在或無法使用。";
				case DriveNotFoundException:
					return "AI Usage 使用的磁碟目前無法使用。";
				case JsonException:
				case InvalidDataException:
				case FormatException:
					return "AI Usage 的本機設定資料格式無法讀取。";
				case IOException:
					return "AI Usage 無法讀取或寫入本機資料；檔案可能正被占用，或磁碟目前無法使用。";
				case NotSupportedException:
					return "AI Usage 無法使用目前的本機設定或資料路徑。";
			}
		}

		return unexpectedReason;
	}

	internal static AppDiagnosticWriteResult TryWrite(
		string operation,
		string summary,
		Exception? exception = null)
	{
		string? filePath = null;

		try
		{
			filePath = GetDiagnosticFilePath();
			return TryWrite(
				filePath,
				operation,
				summary,
				exception,
				DateTimeOffset.UtcNow);
		}
		catch (Exception)
		{
			return new AppDiagnosticWriteResult(filePath, WasWritten: false);
		}
	}

	internal static AppDiagnosticWriteResult TryWrite(
		string filePath,
		string operation,
		string summary,
		Exception? exception,
		DateTimeOffset recordedAt)
	{
		try
		{
			return TryWrite(
				filePath,
				operation,
				summary,
				exception,
				recordedAt,
				GetDiagnosticWriteLockFilePath(filePath),
				DiagnosticWriteLockWaitTimeout);
		}
		catch (Exception)
		{
			return new AppDiagnosticWriteResult(filePath, WasWritten: false);
		}
	}

	internal static AppDiagnosticWriteResult TryWrite(
		string filePath,
		string operation,
		string summary,
		Exception? exception,
		DateTimeOffset recordedAt,
		string lockFilePath,
		TimeSpan lockWaitTimeout)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(filePath) ||
				string.IsNullOrWhiteSpace(operation) ||
				string.IsNullOrWhiteSpace(summary) ||
				string.IsNullOrWhiteSpace(lockFilePath) ||
				(lockWaitTimeout < TimeSpan.Zero))
			{
				return new AppDiagnosticWriteResult(filePath, WasWritten: false);
			}

			string? directoryPath = Path.GetDirectoryName(filePath);

			if (string.IsNullOrWhiteSpace(directoryPath))
			{
				return new AppDiagnosticWriteResult(filePath, WasWritten: false);
			}

			Directory.CreateDirectory(directoryPath);
			using FileStream? writeLock = TryAcquireDiagnosticWriteLock(
				lockFilePath,
				lockWaitTimeout);

			if (writeLock is null)
			{
				return new AppDiagnosticWriteResult(
					filePath,
					WasWritten: false);
			}

			RotateIfNeeded(filePath);
			File.AppendAllText(
				filePath,
				CreateEntry(operation, summary, exception, recordedAt),
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return new AppDiagnosticWriteResult(filePath, WasWritten: true);
		}
		catch (Exception)
		{
			return new AppDiagnosticWriteResult(filePath, WasWritten: false);
		}
	}

	private static bool IsDiagnosticWriteLockContention(IOException exception)
	{
		const int lockViolationErrorCode = 33;
		const int sharingViolationErrorCode = 32;
		int errorCode = exception.HResult & 0xFFFF;
		return (errorCode == sharingViolationErrorCode) ||
			(errorCode == lockViolationErrorCode);
	}

	private static void RotateIfNeeded(string filePath)
	{
		FileInfo file = new(filePath);
		file.Refresh();

		if (!file.Exists || (file.Length < MaximumDiagnosticFileBytes))
		{
			return;
		}

		string previousPath = $"{filePath}.1";
		File.Move(filePath, previousPath, overwrite: true);
	}

	private static FileStream? TryAcquireDiagnosticWriteLock(
		string lockFilePath,
		TimeSpan lockWaitTimeout)
	{
		long waitStartedTimestamp = Stopwatch.GetTimestamp();

		while (true)
		{
			try
			{
				return new FileStream(
					lockFilePath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None);
			}
			catch (IOException exception) when (
				IsDiagnosticWriteLockContention(exception))
			{
				TimeSpan elapsed = Stopwatch.GetElapsedTime(
					waitStartedTimestamp);

				if (elapsed >= lockWaitTimeout)
				{
					return null;
				}

				TimeSpan remaining = lockWaitTimeout - elapsed;
				Thread.Sleep(remaining < DiagnosticWriteLockRetryDelay
					? remaining
					: DiagnosticWriteLockRetryDelay);
			}
		}
	}

	private static string CreateEntry(
		string operation,
		string summary,
		Exception? exception,
		DateTimeOffset recordedAt)
	{
		StringBuilder builder = new();
		builder.Append('[');
		builder.Append(recordedAt.ToUniversalTime().ToString("O"));
		builder.AppendLine("]");
		builder.Append("operation=");
		builder.AppendLine(NormalizeSingleLine(operation));
		builder.Append("summary=");
		builder.AppendLine(NormalizeSingleLine(summary));

		for (Exception? current = exception;
			current is not null;
			current = current.InnerException)
		{
			builder.Append("exception_type=");
			builder.AppendLine(current.GetType().FullName);
			builder.Append("hresult=0x");
			builder.AppendLine(current.HResult.ToString("X8"));

			if (!string.IsNullOrWhiteSpace(current.StackTrace))
			{
				builder.AppendLine("stack_trace:");
				builder.AppendLine(current.StackTrace);
			}
		}

		builder.AppendLine();
		return builder.ToString();
	}

	private static string NormalizeSingleLine(string value)
	{
		return value
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Trim();
	}
}
