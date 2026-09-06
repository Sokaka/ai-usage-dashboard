using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal class CodexCliVersionProbeException : InvalidOperationException
{
	internal CodexCliVersionProbeException(
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed class CodexCliVersionProbeContainmentException :
	CodexCliVersionProbeException
{
	internal CodexCliVersionProbeContainmentException(
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed class CodexVersionProbeContainmentState
{
	private sealed class ExecutableLeaseRetention : IDisposable
	{
		private WindowsOfficialCliExecutableLease? _executableLease;
		private CodexVersionProbeContainmentState? _owner;

		internal ExecutableLeaseRetention(
			CodexVersionProbeContainmentState owner,
			WindowsOfficialCliExecutableLease executableLease)
		{
			_owner = owner ?? throw new ArgumentNullException(nameof(owner));
			_executableLease = executableLease ??
				throw new ArgumentNullException(nameof(executableLease));
		}

		public void Dispose()
		{
			CodexVersionProbeContainmentState? owner = Interlocked.Exchange(
				ref _owner,
				null);
			owner?.ReleaseExecutableLease(this);
		}

		internal WindowsOfficialCliExecutableLease? TakeExecutableLease()
		{
			return Interlocked.Exchange(ref _executableLease, null);
		}
	}

	private sealed class LaunchTransitionLease : IDisposable
	{
		private object? _transitionGate;

		internal LaunchTransitionLease(object transitionGate)
		{
			_transitionGate = transitionGate ??
				throw new ArgumentNullException(nameof(transitionGate));
		}

		public void Dispose()
		{
			object? transitionGate = Interlocked.Exchange(
				ref _transitionGate,
				null);

			if (transitionGate is not null)
			{
				Monitor.Exit(transitionGate);
			}
		}
	}

	private readonly HashSet<ExecutableLeaseRetention> _activeExecutableLeases = new();
	private readonly object _executableLeaseGate = new();
	private readonly object _launchTransitionGate = new();
	private readonly List<WindowsOfficialCliExecutableLease>
		_quarantinedExecutableLeases = new();
	private int _isCompromised;
	private int _isCompromiseRequested;

	internal static CodexVersionProbeContainmentState Shared { get; } = new();

	internal bool IsCompromised =>
		(Volatile.Read(ref _isCompromiseRequested) != 0) ||
		(Volatile.Read(ref _isCompromised) != 0);
	internal int QuarantinedExecutableLeaseCount
	{
		get
		{
			lock (_executableLeaseGate)
			{
				return _quarantinedExecutableLeases.Count;
			}
		}
	}

	internal IDisposable EnterLaunchTransition()
	{
		Monitor.Enter(_launchTransitionGate);

		try
		{
			ThrowIfCompromised();
			return new LaunchTransitionLease(_launchTransitionGate);
		}
		catch
		{
			Monitor.Exit(_launchTransitionGate);
			throw;
		}
	}

	internal void MarkCompromised()
	{
		lock (_executableLeaseGate)
		{
			// 先關閉新 probe；未確認已結束的 process 所用 executable
			// 必須鎖定到本次 App 結束。
			Interlocked.Exchange(ref _isCompromiseRequested, 1);

			foreach (ExecutableLeaseRetention retention in
				_activeExecutableLeases)
			{
				WindowsOfficialCliExecutableLease? executableLease =
					retention.TakeExecutableLease();

				if (executableLease is not null)
				{
					_quarantinedExecutableLeases.Add(executableLease);
				}
			}

			_activeExecutableLeases.Clear();
		}

		lock (_launchTransitionGate)
		{
			Volatile.Write(ref _isCompromised, 1);
		}
	}

	internal IDisposable RetainExecutableLease(
		WindowsOfficialCliExecutableLease executableLease)
	{
		ArgumentNullException.ThrowIfNull(executableLease);
		ExecutableLeaseRetention retention = new(this, executableLease);
		WindowsOfficialCliExecutableLease? rejectedLease = null;

		lock (_executableLeaseGate)
		{
			if (IsCompromised)
			{
				rejectedLease = retention.TakeExecutableLease() ??
					throw new InvalidOperationException(
						"Codex version-probe executable lease ownership was lost.");
			}
			else
			{
				_activeExecutableLeases.Add(retention);
			}
		}

		if (rejectedLease is not null)
		{
			rejectedLease.Dispose();
			ThrowIfCompromised();
		}

		return retention;
	}

	internal void ThrowIfCompromised()
	{
		if (IsCompromised)
		{
			throw new CodexCliVersionProbeContainmentException(
				"Codex version-probe process containment could not be confirmed earlier in this run. Restart AI Usage before using Codex again.");
		}
	}

	private void ReleaseExecutableLease(ExecutableLeaseRetention retention)
	{
		WindowsOfficialCliExecutableLease? executableLease = null;

		lock (_executableLeaseGate)
		{
			if (!_activeExecutableLeases.Remove(retention))
			{
				return;
			}

			executableLease = retention.TakeExecutableLease();

			if ((executableLease is not null) && IsCompromised)
			{
				_quarantinedExecutableLeases.Add(executableLease);
				executableLease = null;
			}
		}

		executableLease?.Dispose();
	}
}

internal static class CodexCliVersionProbe
{
	private const int MaximumStandardErrorBytes = 4 * 1024;
	private const int MaximumStandardOutputBytes = 1024;
	private const string ValidationHomeDirectoryPrefix =
		"probe-";
	private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly CodexVersionProbeContainmentState ContainmentState =
		CodexVersionProbeContainmentState.Shared;
	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
	private static readonly string[] ScrubbedEnvironmentVariables =
	{
		"CODEX_ACCESS_TOKEN",
		"CODEX_API_KEY",
		"OPENAI_API_KEY"
	};
	private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);

	internal static bool HasExpectedVersion(string executablePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		return TryProbeVersion(executablePath, out _);
	}

	internal static bool TryProbeVersion(
		string executablePath,
		out Version version)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		Version? detectedVersion = ProbeAsync(executablePath)
			.GetAwaiter()
			.GetResult();
		version = detectedVersion ?? new Version(0, 0, 0);
		return detectedVersion is not null;
	}

	internal static CodexCliVersionProbeException CreateLaunchFailure(
		WindowsJobContainedProcessLaunchException exception,
		CodexVersionProbeContainmentState containmentState)
	{
		ArgumentNullException.ThrowIfNull(exception);
		ArgumentNullException.ThrowIfNull(containmentState);

		if (!exception.IsTreeEmptyConfirmed || exception.IsLaunchBlocked)
		{
			containmentState.MarkCompromised();
			string message = exception.IsTreeEmptyConfirmed
				? "The Codex version-probe launch was blocked because process containment had already been compromised. Restart AI Usage before using Codex again."
				: "Unable to confirm containment after the Codex version probe failed to start. Restart AI Usage before using Codex again.";
			return new CodexCliVersionProbeContainmentException(
				message,
				exception);
		}

		return new CodexCliVersionProbeException(
			"Unable to start the contained Codex version probe.",
			exception);
	}

	internal static bool TryParseVersion(
		string output,
		out Version version)
	{
		version = new Version(0, 0, 0);

		if (string.IsNullOrWhiteSpace(output))
		{
			return false;
		}

		const string prefix = "codex-cli ";
		string text = output.Trim();

		if (!text.StartsWith(prefix, StringComparison.Ordinal) ||
			text.Contains('\r') ||
			text.Contains('\n') ||
			text.Contains('\0'))
		{
			return false;
		}

		string[] components = text[prefix.Length..].Split(
			'.',
			StringSplitOptions.None);

		if ((components.Length != 3) ||
			!TryParseVersionComponent(components[0], out int major) ||
			!TryParseVersionComponent(components[1], out int minor) ||
			!TryParseVersionComponent(components[2], out int patch) ||
			((major == 0) && (minor == 0) && (patch == 0)))
		{
			return false;
		}

		version = new Version(major, minor, patch);
		return true;
	}

	internal static string CreateValidationHomeRoot()
	{
		string validationHomeBasePath = GetValidationHomeBasePath();

		if (!AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				validationHomeBasePath,
				out _,
				out _) ||
			!WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory(
				validationHomeBasePath) ||
			!WindowsExecutablePathSecurity
				.IsDirectoryPathAclSafeWithinTrustedRoot(
					validationHomeBasePath,
					validationHomeBasePath))
		{
			throw new InvalidOperationException(
				"Codex validation home base is unsafe.");
		}

		string validationHomeRoot = Path.Combine(
			validationHomeBasePath,
			$"{ValidationHomeDirectoryPrefix}{Guid.NewGuid():N}");

		if (!AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				validationHomeRoot,
				out _,
				out _))
		{
			throw new InvalidOperationException(
				"Codex validation home could not be protected.");
		}

		DirectoryInfo validationDirectory = new(validationHomeRoot);
		validationDirectory.Refresh();

		if (!validationDirectory.Exists ||
			((validationDirectory.Attributes & FileAttributes.ReparsePoint) != 0) ||
			!WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory(
				validationDirectory.FullName) ||
			!WindowsExecutablePathSecurity
				.IsDirectoryPathAclSafeWithinTrustedRoot(
					validationDirectory.FullName,
					validationHomeBasePath))
		{
			throw new InvalidOperationException(
				"Codex validation home is unsafe.");
		}

		return validationDirectory.FullName;
	}

	internal static string GetValidationHomeBasePath()
	{
		return Path.Combine(
			CodexCliExecutableStager.ResolveDefaultProviderRoot(),
			"validation-v1");
	}

	internal static bool TryDeleteValidationHomeRoot(string validationHomeRoot)
	{
		try
		{
			string validationHomeBasePath = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(GetValidationHomeBasePath()));
			string fullPath = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(validationHomeRoot));
			string? parentPath = Path.GetDirectoryName(fullPath);
			string directoryName = Path.GetFileName(fullPath);
			string identifierText = directoryName.StartsWith(
				ValidationHomeDirectoryPrefix,
				StringComparison.Ordinal)
					? directoryName[ValidationHomeDirectoryPrefix.Length..]
					: string.Empty;

			if (!string.Equals(
					parentPath,
					validationHomeBasePath,
					StringComparison.OrdinalIgnoreCase) ||
				!directoryName.StartsWith(
					ValidationHomeDirectoryPrefix,
					StringComparison.Ordinal) ||
				!Guid.TryParseExact(identifierText, "N", out _))
			{
				return false;
			}

			if (!WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory(
					validationHomeBasePath) ||
				!WindowsExecutablePathSecurity
					.IsDirectoryPathAclSafeWithinTrustedRoot(
						validationHomeBasePath,
						validationHomeBasePath))
			{
				return false;
			}

			DirectoryInfo validationDirectory = new(fullPath);
			validationDirectory.Refresh();

			if (!validationDirectory.Exists)
			{
				return true;
			}

			if (((validationDirectory.Attributes & FileAttributes.ReparsePoint) != 0) ||
				!WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory(
					validationDirectory.FullName) ||
				!WindowsExecutablePathSecurity
					.IsDirectoryPathAclSafeWithinTrustedRoot(
						validationDirectory.FullName,
						validationHomeBasePath) ||
				ContainsReparsePoint(validationDirectory))
			{
				return false;
			}

			validationDirectory.Delete(recursive: true);
			return !Directory.Exists(fullPath);
		}
		catch
		{
			return false;
		}
	}

	private static async Task<Version?> ProbeAsync(string executablePath)
	{
		ContainmentState.ThrowIfCompromised();
		string validationHomeRoot;

		try
		{
			validationHomeRoot = CreateValidationHomeRoot();
		}
		catch (Exception exception)
		{
			throw new CodexCliVersionProbeException(
				"Unable to create the isolated Codex version-probe home.",
				exception);
		}

		bool canDeleteValidationHome = true;
		bool probeCompleted = false;
		WindowsJobContainedProcess? process = null;

		try
		{
			string codexHome = Path.Combine(validationHomeRoot, "codex-home");
			string sqliteHome = Path.Combine(validationHomeRoot, "sqlite-home");
			string isolatedProfile = Path.Combine(
				validationHomeRoot,
				"profile");
			Directory.CreateDirectory(codexHome);
			Directory.CreateDirectory(sqliteHome);
			Directory.CreateDirectory(isolatedProfile);
			ProcessStartInfo startInfo = new(executablePath)
			{
				UseShellExecute = false,
				WorkingDirectory = isolatedProfile
			};
			startInfo.ArgumentList.Add("--version");

			foreach (string variableName in ScrubbedEnvironmentVariables)
			{
				startInfo.Environment.Remove(variableName);
			}

			startInfo.Environment["CODEX_HOME"] = codexHome;
			startInfo.Environment["CODEX_SQLITE_HOME"] = sqliteHome;
			startInfo.Environment["HOME"] = isolatedProfile;
			startInfo.Environment["NO_COLOR"] = "1";
			startInfo.Environment["USERPROFILE"] = isolatedProfile;

			using (IDisposable transition =
				ContainmentState.EnterLaunchTransition())
			{
				try
				{
					ContainmentState.ThrowIfCompromised();
					process = WindowsJobContainedProcess.Start(
						startInfo,
						ContainmentState.ThrowIfCompromised);
					canDeleteValidationHome = false;
				}
				catch (WindowsJobContainedProcessLaunchException exception)
				{
					canDeleteValidationHome = exception.IsTreeEmptyConfirmed;
					throw CreateLaunchFailure(exception, ContainmentState);
				}
			}

			Task<byte[]> standardOutputTask = ReadBoundedAsync(
				process.StandardOutput,
				MaximumStandardOutputBytes,
				CancellationToken.None);
			Task<byte[]> standardErrorTask = ReadBoundedAsync(
				process.StandardError,
				MaximumStandardErrorBytes,
				CancellationToken.None);
			Task<int> rootExitTask = process.WaitForExitAsync(
				CancellationToken.None);
			Task redirectedStreamTasks = Task.WhenAll(
				standardOutputTask,
				standardErrorTask);
			Exception? probeFailure = null;
			int? exitCode = null;

			try
			{
				exitCode = await rootExitTask.WaitAsync(ProbeTimeout);
			}
			catch (TimeoutException exception)
			{
				probeFailure = new TimeoutException(
					"The contained Codex version probe timed out.",
					exception);
			}
			catch (Exception exception)
			{
				probeFailure = exception;
			}

			Exception? cleanupFailure = null;

			try
			{
				await process.TerminateTreeAndConfirmEmptyAsync(CleanupTimeout);
				canDeleteValidationHome = true;
			}
			catch (Exception exception)
			{
				cleanupFailure = exception;
			}

			try
			{
				await redirectedStreamTasks.WaitAsync(CleanupTimeout);
			}
			catch (Exception exception)
			{
				probeFailure ??= exception;
			}

			if (!rootExitTask.IsCompleted)
			{
				ProviderProcessExecution.ObserveFault(rootExitTask);
			}

			ProviderProcessExecution.ObserveFault(redirectedStreamTasks);

			if (cleanupFailure is not null)
			{
				ContainmentState.MarkCompromised();
				Exception failure = probeFailure is null
					? cleanupFailure
					: new AggregateException(probeFailure, cleanupFailure);
				throw new CodexCliVersionProbeContainmentException(
					"The Codex version-probe process tree did not reach confirmed quiescence. Restart AI Usage before using Codex again.",
					failure);
			}

			if (probeFailure is not null)
			{
				throw new CodexCliVersionProbeException(
					"The Codex version probe could not complete.",
					probeFailure);
			}

			if (exitCode is null)
			{
				throw new CodexCliVersionProbeException(
					"The Codex version probe exit code was unavailable.");
			}

			if (exitCode.Value != 0)
			{
				throw new CodexCliVersionProbeException(
					$"The Codex version probe exited with code {exitCode.Value}.");
			}

			string output = StrictUtf8Encoding.GetString(
				await standardOutputTask);
			Version? detectedVersion = TryParseVersion(
				output,
				out Version parsedVersion)
					? parsedVersion
					: null;
			probeCompleted = true;
			return detectedVersion;
		}
		catch (CodexCliVersionProbeException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new CodexCliVersionProbeException(
				"The Codex version probe failed operationally.",
				exception);
		}
		finally
		{
			process?.Dispose();

			if (canDeleteValidationHome)
			{
				bool isDeleted = TryDeleteValidationHomeRoot(
					validationHomeRoot);

				if (probeCompleted && !isDeleted)
				{
					throw new CodexCliVersionProbeException(
						"The isolated Codex version-probe home could not be removed safely.");
				}
			}
		}
	}

	private static async Task<byte[]> ReadBoundedAsync(
		Stream stream,
		int maximumBytes,
		CancellationToken cancellationToken)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
		using MemoryStream captured = new(maximumBytes);

		try
		{
			while (true)
			{
				int bytesRead = await stream.ReadAsync(
					buffer.AsMemory(0, Math.Min(buffer.Length, 1024)),
					cancellationToken);

				if (bytesRead == 0)
				{
					return captured.ToArray();
				}

				if ((captured.Length + bytesRead) > maximumBytes)
				{
					throw new InvalidDataException(
						"Codex version output exceeded its safety bound.");
				}

				captured.Write(buffer, 0, bytesRead);
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

	private static bool ContainsReparsePoint(DirectoryInfo root)
	{
		Stack<DirectoryInfo> pendingDirectories = new();
		pendingDirectories.Push(root);

		while (pendingDirectories.TryPop(out DirectoryInfo? directory))
		{
			if (directory is null)
			{
				return true;
			}

			directory.Refresh();

			if (!directory.Exists ||
				((directory.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return true;
			}

			foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
			{
				entry.Refresh();

				if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					return true;
				}

				if (entry is DirectoryInfo childDirectory)
				{
					pendingDirectories.Push(childDirectory);
				}
			}
		}

		return false;
	}

}
