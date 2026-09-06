using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;

using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.App.Providers;

internal interface IGrokCliVersionProbe
{
	Task<GrokExecutableVersion?> ReadVersionAsync(
		string executablePath,
		CancellationToken cancellationToken = default,
		ProviderProcessOperationTracker? operationTracker = null);
}

internal sealed class GrokCliVersionProbe : IGrokCliVersionProbe
{
	private sealed class GrokVersionOutputException : IOException
	{
		internal GrokVersionOutputException(string message)
			: base(message)
		{
		}
	}

	private sealed class ScratchCleanupLease : IDisposable
	{
		private Action? _cleanup;

		internal ScratchCleanupLease(Action cleanup)
		{
			_cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _cleanup, null)?.Invoke();
		}
	}

	private const int MaximumCommitLength = 64;
	private const int MaximumStandardErrorBytes = 8 * 1024;
	private const int MaximumStandardOutputBytes = 1024;
	private const int MinimumCommitLength = 7;
	private static readonly TimeSpan DefaultCleanupTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly TimeSpan DefaultProbeTimeout =
		TimeSpan.FromSeconds(5);
	private static readonly UTF8Encoding StrictUtf8 = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);
	private readonly TimeSpan _cleanupTimeout;
	private readonly Action<string> _cleanupScratchDirectory;
	private readonly Func<string> _getScratchRootDirectory;
	private readonly Action<string, string> _prepareDirectories;
	private readonly TimeSpan _probeTimeout;
	private readonly IGrokAcpProcessFactory _processFactory;

	internal GrokCliVersionProbe(IGrokAcpProcessFactory processFactory)
		: this(
			processFactory,
			AppDataPaths.GetGrokVersionProbeRootDirectory,
			GrokAcpUsagePoller.PreparePrivateDirectories,
			CleanupScratchDirectory,
			DefaultProbeTimeout,
			DefaultCleanupTimeout)
	{
	}

	internal GrokCliVersionProbe(
		IGrokAcpProcessFactory processFactory,
		Func<string> getScratchRootDirectory,
		Action<string, string> prepareDirectories,
		Action<string> cleanupScratchDirectory,
		TimeSpan probeTimeout,
		TimeSpan cleanupTimeout)
	{
		_processFactory = processFactory ??
			throw new ArgumentNullException(nameof(processFactory));
		_getScratchRootDirectory = getScratchRootDirectory ??
			throw new ArgumentNullException(nameof(getScratchRootDirectory));
		_prepareDirectories = prepareDirectories ??
			throw new ArgumentNullException(nameof(prepareDirectories));
		_cleanupScratchDirectory = cleanupScratchDirectory ??
			throw new ArgumentNullException(nameof(cleanupScratchDirectory));
		_probeTimeout = probeTimeout > TimeSpan.Zero
			? probeTimeout
			: throw new ArgumentOutOfRangeException(nameof(probeTimeout));
		_cleanupTimeout = cleanupTimeout > TimeSpan.Zero
			? cleanupTimeout
			: throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
	}

	public async Task<GrokExecutableVersion?> ReadVersionAsync(
		string executablePath,
		CancellationToken cancellationToken = default,
		ProviderProcessOperationTracker? operationTracker = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		cancellationToken.ThrowIfCancellationRequested();
		string fullExecutablePath = Path.GetFullPath(executablePath);

		if (!Path.IsPathFullyQualified(executablePath))
		{
			throw new ArgumentException(
				"Grok version probe requires an absolute executable path.",
				nameof(executablePath));
		}

		Guid attemptId = Guid.NewGuid();
		string scratchRoot = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(_getScratchRootDirectory()));
		string scratchDirectory = Path.GetFullPath(
			Path.Combine(scratchRoot, attemptId.ToString("N")));
		ValidateScratchDirectory(scratchRoot, scratchDirectory, attemptId);
		string homeDirectory = Path.Combine(scratchDirectory, "home");
		string workingDirectory = Path.Combine(
			scratchDirectory,
			"blank-workspace");
		ProviderProcessOperationTracker processOperationTracker =
			operationTracker ?? new ProviderProcessOperationTracker();
		IDisposable scratchCleanupLease = processOperationTracker.HoldLease(
			new ScratchCleanupLease(
				() => _cleanupScratchDirectory(scratchDirectory)));
		using CancellationTokenSource timeoutSource = new(_probeTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);

		try
		{
			_prepareDirectories(homeDirectory, workingDirectory);
			await using IGrokAcpProcess process =
				await GrokAcpProcessExecution.StartAsync(
					_processFactory,
					new GrokAcpLaunchOptions(
						fullExecutablePath,
						homeDirectory,
						workingDirectory,
						GrokContainedCommand.Version),
					linkedSource.Token,
					processOperationTracker);
			process.StandardInput.Dispose();
			Task<byte[]> standardOutputTask = ReadBoundedAsync(
				process.StandardOutput,
				MaximumStandardOutputBytes,
				captureOutput: true,
				linkedSource.Token);
			Task<byte[]> standardErrorTask = ReadBoundedAsync(
				process.StandardError,
				MaximumStandardErrorBytes,
				captureOutput: false,
				linkedSource.Token);
			Task<int> exitTask = process.WaitForExitAsync(linkedSource.Token);

			try
			{
				await Task.WhenAll(
					standardOutputTask,
					standardErrorTask,
					exitTask);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException exception) when (
				timeoutSource.IsCancellationRequested)
			{
				throw new GrokCliVersionProbeException(
					"Grok CLI version probe exceeded its deadline.",
					exception);
			}
			catch (GrokVersionOutputException)
			{
				return null;
			}

			using CancellationTokenSource cleanupSource = new(_cleanupTimeout);
			await process.TerminateTreeAsync(cleanupSource.Token);

			if (await exitTask != 0)
			{
				return null;
			}

			return ParseVersionOutput(await standardOutputTask);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException exception) when (
			timeoutSource.IsCancellationRequested)
		{
			throw new GrokCliVersionProbeException(
				"Grok CLI version probe exceeded its deadline.",
				exception);
		}
		catch (GrokCliVersionProbeException)
		{
			throw;
		}
		catch (GrokProcessContainmentException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new GrokCliVersionProbeException(
				"Unable to complete the contained Grok CLI version probe.",
				exception);
		}
		finally
		{
			scratchCleanupLease.Dispose();
		}
	}

	internal static GrokExecutableVersion? ParseVersionOutput(
		ReadOnlySpan<byte> output)
	{
		string text;

		try
		{
			text = StrictUtf8.GetString(output);
		}
		catch (DecoderFallbackException)
		{
			return null;
		}

		if (text.EndsWith("\r\n", StringComparison.Ordinal))
		{
			text = text[..^2];
		}
		else if (text.EndsWith('\n'))
		{
			text = text[..^1];
		}
		else if (text.EndsWith('\r'))
		{
			return null;
		}

		if (!text.StartsWith("grok ", StringComparison.Ordinal) ||
			text.Contains('\r') ||
			text.Contains('\n') ||
			text.Contains('\0') ||
			!text.EndsWith(')'))
		{
			return null;
		}

		int commitSeparator = text.IndexOf(" (", StringComparison.Ordinal);

		if (commitSeparator <= "grok ".Length)
		{
			return null;
		}

		string versionText = text["grok ".Length..commitSeparator];
		string commit = text[(commitSeparator + 2)..^1];

		if ((commit.Length < MinimumCommitLength) ||
			(commit.Length > MaximumCommitLength) ||
			commit.Any(character =>
				!char.IsAsciiHexDigit(character) || char.IsUpper(character)))
		{
			return null;
		}

		string[] components = versionText.Split('.', StringSplitOptions.None);

		return (components.Length == 3) &&
			TryParseVersionComponent(components[0], out int major) &&
			TryParseVersionComponent(components[1], out int minor) &&
			TryParseVersionComponent(components[2], out int patch)
				? new GrokExecutableVersion(major, minor, patch)
				: null;
	}

	private static async Task<byte[]> ReadBoundedAsync(
		Stream stream,
		int maximumBytes,
		bool captureOutput,
		CancellationToken cancellationToken)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
		using MemoryStream captured = new(
			captureOutput ? maximumBytes : 0);
		int totalBytes = 0;

		try
		{
			while (true)
			{
				int bytesRead = await stream.ReadAsync(
					buffer.AsMemory(0, Math.Min(buffer.Length, 1024)),
					cancellationToken);

				if (bytesRead == 0)
				{
					return captureOutput
						? captured.ToArray()
						: Array.Empty<byte>();
				}

				totalBytes = checked(totalBytes + bytesRead);

				if (totalBytes > maximumBytes)
				{
					throw new GrokVersionOutputException(
						"Grok CLI version output exceeded its safety bound.");
				}

				if (captureOutput)
				{
					captured.Write(buffer, 0, bytesRead);
				}
			}
		}
		finally
		{
			Array.Clear(buffer, 0, buffer.Length);
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static bool TryParseVersionComponent(
		string value,
		out int result)
	{
		result = 0;

		if (string.IsNullOrEmpty(value) ||
			((value.Length > 1) && (value[0] == '0')))
		{
			return false;
		}

		return int.TryParse(
			value,
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out result);
	}

	private static void ValidateScratchDirectory(
		string scratchRoot,
		string scratchDirectory,
		Guid attemptId)
	{
		string? parent = Path.GetDirectoryName(scratchDirectory);

		if (string.IsNullOrWhiteSpace(scratchRoot) ||
			!Path.IsPathFullyQualified(scratchRoot) ||
			!string.Equals(
				parent,
				scratchRoot,
				StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(
				Path.GetFileName(scratchDirectory),
				attemptId.ToString("N"),
				StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				"Grok version probe scratch path is invalid.");
		}
	}

	private static void CleanupScratchDirectory(string scratchDirectory)
	{
		try
		{
			DirectoryInfo root = new(scratchDirectory);

			if (!root.Exists ||
				((root.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return;
			}

			Stack<DirectoryInfo> directories = new();
			directories.Push(root);

			while (directories.TryPop(out DirectoryInfo? directory))
			{
				foreach (FileSystemInfo item in
					directory.EnumerateFileSystemInfos())
				{
					if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
					{
						return;
					}

					if (item is DirectoryInfo childDirectory)
					{
						directories.Push(childDirectory);
					}
				}
			}

			root.Delete(recursive: true);
		}
		catch
		{
			// This credential-free scratch tree is never reused or loaded.
		}
	}
}
