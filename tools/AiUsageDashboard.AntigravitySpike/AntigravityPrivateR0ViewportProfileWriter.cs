using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal static class AntigravityPrivateR0ViewportProfileWriter
{
	internal const int MaximumColumns = 1000;
	internal const int MaximumRows = 256;
	private const int MaximumProfileBytes = 1024 * 1024;
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(
				namingPolicy: null,
				allowIntegerValues: false)
		}
	};

	internal static bool IsSafeOutputPath(
		string sourceProfilePath,
		string keyPath,
		string outputPath)
	{
		try
		{
			if (!Path.IsPathFullyQualified(sourceProfilePath) ||
				!Path.IsPathFullyQualified(keyPath) ||
				!Path.IsPathFullyQualified(outputPath) ||
				!string.Equals(
					Path.GetExtension(sourceProfilePath),
					".json",
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					Path.GetExtension(outputPath),
					".json",
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(
					Path.GetExtension(keyPath),
					".key",
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string absoluteSourcePath = Path.GetFullPath(sourceProfilePath);
			string absoluteKeyPath = Path.GetFullPath(keyPath);
			string absoluteOutputPath = Path.GetFullPath(outputPath);
			string sourceDirectory =
				Path.GetDirectoryName(absoluteSourcePath) ?? string.Empty;
			string keyDirectory =
				Path.GetDirectoryName(absoluteKeyPath) ?? string.Empty;
			string outputDirectory =
				Path.GetDirectoryName(absoluteOutputPath) ?? string.Empty;

			return
				!string.Equals(
					absoluteSourcePath,
					absoluteOutputPath,
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(
					sourceDirectory,
					keyDirectory,
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(
					sourceDirectory,
					outputDirectory,
					StringComparison.OrdinalIgnoreCase) &&
				!Directory.Exists(absoluteOutputPath) &&
				AntigravityPrivateKeyAcl.IsPrivateDirectory(
					outputDirectory);
		}
		catch
		{
			return false;
		}
	}

	internal static bool AreValidDimensions(int columns, int rows)
	{
		return
			(columns >= 1) &&
			(columns <= MaximumColumns) &&
			(rows >= 1) &&
			(rows <= MaximumRows);
	}

	internal static async Task<bool> TryWriteAsync(
		string outputPath,
		AntigravityLiveR0ProfileFile profileFile,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profileFile);
		byte[]? serializedBytes = null;
		string? temporaryPath = null;
		bool ownsTemporaryFile = false;

		try
		{
			serializedBytes = JsonSerializer.SerializeToUtf8Bytes(
				profileFile,
				WriteOptions);

			if ((serializedBytes.Length == 0) ||
				(serializedBytes.Length > MaximumProfileBytes) ||
				!Path.IsPathFullyQualified(outputPath))
			{
				return false;
			}

			string absoluteOutputPath = Path.GetFullPath(outputPath);
			string? parentPath = Path.GetDirectoryName(absoluteOutputPath);

			if ((parentPath is null) ||
				!AntigravityPrivateKeyAcl.IsPrivateDirectory(parentPath))
			{
				return false;
			}

			if (File.Exists(absoluteOutputPath))
			{
				return await HasExactExistingBytesAsync(
					absoluteOutputPath,
					serializedBytes,
					cancellationToken);
			}

			temporaryPath = Path.Combine(
				parentPath,
				$".{Path.GetFileName(absoluteOutputPath)}.derive-viewport.{Guid.NewGuid():N}.tmp");

			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				ownsTemporaryFile = true;

				if (!AntigravityPrivateKeyAcl.TryProtectNewFile(temporaryPath))
				{
					return false;
				}

				await stream.WriteAsync(serializedBytes, cancellationToken);
				await stream.FlushAsync(cancellationToken);
				stream.Flush(flushToDisk: true);
			}

			if (!await HasExactExistingBytesAsync(
					temporaryPath,
					serializedBytes,
					cancellationToken))
			{
				return false;
			}

			if (File.Exists(absoluteOutputPath))
			{
				return await HasExactExistingBytesAsync(
					absoluteOutputPath,
					serializedBytes,
					cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				File.Move(temporaryPath, absoluteOutputPath);
				ownsTemporaryFile = false;
				temporaryPath = null;
			}
			catch (IOException)
			{
				return File.Exists(absoluteOutputPath) &&
					await HasExactExistingBytesAsync(
						absoluteOutputPath,
						serializedBytes,
						cancellationToken);
			}

			return
				AntigravityPrivateKeyAcl.IsPrivateFile(absoluteOutputPath) &&
				await HasExactExistingBytesAsync(
					absoluteOutputPath,
					serializedBytes,
					cancellationToken);
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
			if (ownsTemporaryFile && (temporaryPath is not null))
			{
				TryDeleteTemporaryFile(temporaryPath);
			}

			if (serializedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(serializedBytes);
			}
		}
	}

	private static async Task<bool> HasExactExistingBytesAsync(
		string path,
		ReadOnlyMemory<byte> expectedBytes,
		CancellationToken cancellationToken)
	{
		byte[]? existingBytes = null;

		try
		{
			existingBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					path,
					MaximumProfileBytes,
					cancellationToken);
			_ = AntigravityLiveR0ProfileJson.Deserialize(existingBytes);
			return existingBytes.AsSpan().SequenceEqual(expectedBytes.Span);
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
			if (existingBytes is not null)
			{
				CryptographicOperations.ZeroMemory(existingBytes);
			}
		}
	}

	private static void TryDeleteTemporaryFile(string path)
	{
		try
		{
			FileInfo file = new(path);
			file.Refresh();

			if (file.Exists &&
				((file.Attributes &
					(FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0))
			{
				File.Delete(path);
			}
		}
		catch
		{
			// Best-effort cleanup must not mask the fail-closed result.
		}
	}
}
