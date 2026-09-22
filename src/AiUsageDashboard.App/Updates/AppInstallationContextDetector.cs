using System.IO;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.App.Updates;

internal sealed class AppInstallationContextDetector
{
	private const string DiagnosticOperation = "update-installation-context";
	private const string ManagedExecutablePathDescription =
		"The running update-managed application path";
	private const string ManagedManifestPathDescription =
		"The adjacent installed update manifest path";
	private readonly Func<string?> _getLocalApplicationDataDirectory;
	private readonly Action<string, Exception?> _reportDiagnostic;

	internal AppInstallationContextDetector()
		: this(GetLocalApplicationDataDirectory, ReportDiagnostic)
	{
	}

	internal AppInstallationContextDetector(
		Func<string?> getLocalApplicationDataDirectory,
		Action<string, Exception?>? reportDiagnostic = null)
	{
		_getLocalApplicationDataDirectory =
			getLocalApplicationDataDirectory ??
			throw new ArgumentNullException(
				nameof(getLocalApplicationDataDirectory));
		_reportDiagnostic = reportDiagnostic ?? ReportDiagnostic;
	}

	internal async Task<AppInstallationContext> DetectAsync(
		string? currentProcessPath,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? normalizedExecutablePath = null;

		try
		{
			if (string.IsNullOrWhiteSpace(currentProcessPath) ||
				!Path.IsPathFullyQualified(currentProcessPath))
			{
				return AppInstallationContext.CreateUnmanaged();
			}

			normalizedExecutablePath = Path.GetFullPath(currentProcessPath);
			if (!string.Equals(
					Path.GetFileName(normalizedExecutablePath),
					WindowsLogonStartupRegistrationContract.ApplicationFileName,
					StringComparison.OrdinalIgnoreCase))
			{
				return AppInstallationContext.CreateUnmanaged(
					normalizedExecutablePath);
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				normalizedExecutablePath,
				ManagedExecutablePathDescription);
			if (!File.Exists(normalizedExecutablePath))
			{
				return AppInstallationContext.CreateUnmanaged(
					normalizedExecutablePath);
			}

			if (!TryDeriveManagedLayout(
					normalizedExecutablePath,
					out string installRoot,
					out string manifestPath))
			{
				return AppInstallationContext.CreateUnmanaged(
					normalizedExecutablePath);
			}

			ManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				manifestPath,
				ManagedManifestPathDescription);
			if (!File.Exists(manifestPath))
			{
				return AppInstallationContext.CreateUnmanaged(
					normalizedExecutablePath);
			}

			UpdateManifest manifest = await UpdateManifest.ReadAsync(
				manifestPath,
				cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(manifest.PayloadGenerationId))
			{
				throw new InvalidDataException(
					"The installed manifest cannot identify its payload generation.");
			}

			string? localApplicationDataDirectory =
				_getLocalApplicationDataDirectory();
			if (string.IsNullOrWhiteSpace(localApplicationDataDirectory) ||
				!Path.IsPathFullyQualified(localApplicationDataDirectory))
			{
				throw new InvalidOperationException(
					"The current user's local application data directory is unavailable.");
			}

			AppInstallationKind kind =
				WindowsLogonStartupRegistrationContract.IsCanonicalExecutablePath(
					normalizedExecutablePath,
					localApplicationDataDirectory)
					? AppInstallationKind.CanonicalManaged
					: AppInstallationKind.CustomManaged;

			return new AppInstallationContext(
				kind,
				normalizedExecutablePath,
				installRoot,
				manifestPath,
				manifest);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			TryReportDiagnostic(
				"The running application could not be classified as a managed installation.",
				exception);
			return AppInstallationContext.CreateUnmanaged(
				normalizedExecutablePath);
		}
	}

	private static string? GetLocalApplicationDataDirectory()
	{
		return Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
	}

	private static void ReportDiagnostic(
		string summary,
		Exception? exception)
	{
		_ = AppDiagnostics.TryWrite(
			DiagnosticOperation,
			summary,
			exception);
	}

	private static bool TryDeriveManagedLayout(
		string executablePath,
		out string installRoot,
		out string manifestPath)
	{
		installRoot = string.Empty;
		manifestPath = string.Empty;
		DirectoryInfo? appDirectory = Directory.GetParent(executablePath);
		DirectoryInfo? currentDirectory = appDirectory?.Parent;
		DirectoryInfo? rootDirectory = currentDirectory?.Parent;

		if ((appDirectory is null) ||
			(currentDirectory is null) ||
			(rootDirectory is null) ||
			!string.Equals(
				appDirectory.Name,
				"app",
				StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(
				currentDirectory.Name,
				"current",
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		installRoot = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(rootDirectory.FullName));
		string expectedExecutablePath = Path.Combine(
			installRoot,
			"current",
			"app",
			WindowsLogonStartupRegistrationContract.ApplicationFileName);

		if (!string.Equals(
				executablePath,
				Path.GetFullPath(expectedExecutablePath),
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		manifestPath = Path.Combine(
			installRoot,
			"current",
			UpdateManifest.InstalledFileName);
		return true;
	}

	private void TryReportDiagnostic(
		string summary,
		Exception exception)
	{
		try
		{
			_reportDiagnostic(summary, exception);
		}
		catch (Exception diagnosticException)
		{
			_ = AppDiagnostics.TryWrite(
				DiagnosticOperation,
				"The update installation diagnostic callback failed.",
				diagnosticException);
		}
	}
}
