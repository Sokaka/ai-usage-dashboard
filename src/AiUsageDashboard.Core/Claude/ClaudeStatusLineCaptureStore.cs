using System.Text.Json;

namespace AiUsageDashboard.Core.Claude;

public sealed class ClaudeStatusLineCaptureStore
{
	private const int MaximumCaptureFileSize = 1024 * 1024;
	private const int MaximumWriteLockAttempts = 100;
	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};
	private static readonly TimeSpan WriteLockRetryDelay = TimeSpan.FromMilliseconds(20);

	private readonly string _filePath;
	private readonly TimeProvider _timeProvider;

	public ClaudeStatusLineCaptureStore(
		string filePath,
		TimeProvider? timeProvider = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = Path.GetFullPath(filePath);
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task<ClaudeStatusLineCapture?> ReadAsync(
		CancellationToken cancellationToken = default)
	{
		try
		{
			await using FileStream stream = new(
				_filePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete,
				bufferSize: 4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan);

			if (stream.Length > MaximumCaptureFileSize)
			{
				throw new InvalidDataException(
					"Claude status line capture is too large.");
			}

			ClaudeStatusLineCapture? capture =
				await JsonSerializer.DeserializeAsync<ClaudeStatusLineCapture>(
					stream,
					SerializerOptions,
					cancellationToken);

			if (capture is null)
			{
				throw new InvalidDataException("Claude status line capture is empty.");
			}

			ClaudeStatusLineCaptureValidator.Validate(
				capture,
				_timeProvider.GetUtcNow());
			return capture;
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}
	}

	public async Task WriteAsync(
		ClaudeStatusLineCapture capture,
		CancellationToken cancellationToken = default)
	{
		ClaudeStatusLineCaptureValidator.Validate(
			capture,
			_timeProvider.GetUtcNow());

		string directory = Path.GetDirectoryName(_filePath)!;
		Directory.CreateDirectory(directory);
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(capture, SerializerOptions);

		if (json.Length > MaximumCaptureFileSize)
		{
			throw new InvalidDataException(
				"Claude status line capture is too large.");
		}

		await using FileStream writeLock = await AcquireWriteLockAsync(cancellationToken);
		ClaudeStatusLineCapture? existingCapture =
			await ReadExistingCaptureForWriteAsync(cancellationToken);

		if ((existingCapture is not null) &&
			(existingCapture.CapturedAt >= capture.CapturedAt))
		{
			return;
		}

		string temporaryPath = Path.Combine(
			directory,
			$".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

		try
		{
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

			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, _filePath, overwrite: true);
		}
		finally
		{
			try
			{
				File.Delete(temporaryPath);
			}
			catch (Exception exception) when (
				(exception is IOException) ||
				(exception is UnauthorizedAccessException) ||
				(exception is NotSupportedException))
			{
				// Best effort cleanup. Capture readers never inspect temporary files.
			}
		}
	}

	private async Task<FileStream> AcquireWriteLockAsync(
		CancellationToken cancellationToken)
	{
		string lockFilePath = $"{_filePath}.lock";

		for (int attempt = 0; ; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				return new FileStream(
					lockFilePath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					FileOptions.Asynchronous | FileOptions.DeleteOnClose);
			}
			catch (Exception exception) when (
				((exception is IOException) ||
				 (exception is UnauthorizedAccessException)) &&
				(attempt < (MaximumWriteLockAttempts - 1)))
			{
				await Task.Delay(WriteLockRetryDelay, cancellationToken);
			}
		}
	}

	private async Task<ClaudeStatusLineCapture?> ReadExistingCaptureForWriteAsync(
		CancellationToken cancellationToken)
	{
		try
		{
			return await ReadAsync(cancellationToken);
		}
		catch (Exception exception) when (
			(exception is JsonException) ||
			(exception is InvalidDataException))
		{
			return null;
		}
	}
}
