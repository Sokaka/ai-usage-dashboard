using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security;

using AiUsageDashboard.Updater.Core;
using AiUsageDashboard.Licensing;

namespace AiUsageDashboard.Updater;

internal static class Program
{
	private const int CommandLineFailureExitCode = 2;
	private const int GeneralFailureExitCode = 1;
	private const int RestartFailureExitCode = 3;
	private const int SuccessExitCode = 0;

	public static async Task<int> Main(string[] arguments)
	{
		bool shouldNotifyUser = arguments.Length == 0;

		try
		{
			if (LegalCommandLine.IsLegalCommand(arguments))
			{
				LegalCommandLine.TryHandle(arguments, () => LegalCatalog.Load(LegalProfile.Installer),
					LegalAcceptanceStore.CreateDefault, out int legalExitCode);
				return legalExitCode;
			}

			UpdaterCommandLineOptions options = UpdaterCommandLine.Parse(arguments);
			shouldNotifyUser = options.ShouldNotifyUser;
			RecoverInterruptedInstallation(options.InstallRoot);
			if (options.Command == UpdaterCommand.UpdateOnline)
			{
				LegalCatalog catalog = LegalCatalog.Load(LegalProfile.Installer);
				LegalAcceptanceStore store = LegalAcceptanceStore.CreateDefault();
				bool canUpdate = options.ShouldPromptForLicenses
					? LegalNativeTermsDialog.EnsureAccepted(catalog, store)
					: store.IsAccepted(catalog);
				if (!canUpdate)
				{
					Console.Error.WriteLine("更新尚未開始。請先用 --licenses 閱讀本版條款，再以 --accept-licenses <digest> 接受。");
					return LegalCommandLine.AcceptanceRequiredExitCode;
				}
			}
			UpdaterExecutionResult result = await ExecuteAsync(
				options,
				CancellationToken.None);

			if (options.ShouldNotifyUser)
			{
				if (result.ExitCode == SuccessExitCode)
				{
					UpdaterUserNotifier.ShowSuccess(result.UserMessage);
				}
				else
				{
					UpdaterUserNotifier.ShowError(result.UserMessage);
				}
			}

			return result.ExitCode;
		}
		catch (UpdaterCommandLineException exception)
		{
			Console.Error.WriteLine(exception.Message);
			Console.Error.WriteLine(UpdaterCommandLine.Usage);
			NotifyFailureIfRequested(shouldNotifyUser, exception.Message);
			return CommandLineFailureExitCode;
		}
		catch (OperationCanceledException)
		{
			const string message = "The update was cancelled.";
			Console.Error.WriteLine(message);
			NotifyFailureIfRequested(shouldNotifyUser, message);
			return GeneralFailureExitCode;
		}
		catch (Exception exception)
		{
			string message = $"Update failed: {exception.Message}";
			Console.Error.WriteLine(message);
			NotifyFailureIfRequested(shouldNotifyUser, message);
			return GeneralFailureExitCode;
		}
	}

	private static Task<UpdaterExecutionResult> ExecuteAsync(
		UpdaterCommandLineOptions options,
		CancellationToken cancellationToken)
	{
		return options.Command switch
		{
			UpdaterCommand.ApplyLocal => ApplyLocalAsync(
				options,
				cancellationToken),
			UpdaterCommand.UpdateOnline => UpdateOnlineAsync(
				options,
				cancellationToken),
			UpdaterCommand.Uninstall => UninstallAsync(
				options,
				cancellationToken),
			_ => throw new InvalidOperationException(
				"The updater command is not supported.")
		};
	}

	private static async Task<UpdaterExecutionResult> ApplyLocalAsync(
		UpdaterCommandLineOptions options,
		CancellationToken cancellationToken)
	{
		if ((options.PackagePath is null) || (options.Sha256Path is null))
		{
			throw new InvalidOperationException(
				"The local update command is incomplete.");
		}

		UpdateExecutionSafety.EnsureNotElevated();
		LocalUpdatePackage package = await LocalUpdatePackage.ReadAsync(
			options.PackagePath,
			options.Sha256Path,
			cancellationToken);
		return await ApplyPackageAsync(
			package.PackagePath,
			package.Manifest,
			options,
			enforceOnlinePolicy: false,
			cancellationToken);
	}

	private static async Task<UpdaterExecutionResult> UpdateOnlineAsync(
		UpdaterCommandLineOptions options,
		CancellationToken cancellationToken)
	{
		if (options.FeedUri is null)
		{
			throw new InvalidOperationException(
				"The online update command has no feed URL.");
		}

		UpdateExecutionSafety.EnsureNotElevated();
		Console.WriteLine($"Checking the {options.Channel} update feed...");

		using HttpClient httpClient = CreateHttpClient();
		OnlineUpdateClient onlineClient = new(httpClient);
		ResolvedUpdateReleaseFeed resolvedFeed =
			await onlineClient.FetchFeedAsync(
				options.FeedUri,
				options.Channel,
				cancellationToken);
		UpdateReleaseFeed feed = resolvedFeed.Feed;
		UpdateManifest availableManifest = feed.CreatePackageManifest();
		UpdateManifest? installedManifest = await ReadInstalledManifestAsync(
			options.InstallRoot,
			cancellationToken);
		OnlinePayloadUpdateAction preflightAction =
			OnlinePayloadUpdatePolicy.Evaluate(
				installedManifest,
				availableManifest);
		CurrentUpdaterIdentity currentUpdater =
			await CurrentUpdaterIdentity.ReadAsync(
				Environment.ProcessPath ?? throw new InvalidOperationException(
					"The running updater executable path is unavailable."),
				cancellationToken);

		if (options.ShouldRefreshUpdater)
		{
			UpdaterRefreshAction refreshAction = UpdaterRefreshPolicy.Evaluate(
				currentUpdater,
				feed.Updater);

			if (refreshAction ==
				UpdaterRefreshAction.DelegateToDownloadedUpdater)
			{
				Console.WriteLine(
					$"Downloading updater {feed.Updater.Version}...");
				using VerifiedUpdaterArtifactLease delegatedUpdater =
					await new UpdaterArtifactCache(onlineClient)
						.GetOrDownloadAsync(
							feed.Updater,
							options.InstallRoot,
							cancellationToken);
				int delegatedExitCode = await RunDelegatedUpdaterAsync(
					delegatedUpdater.ExecutablePath,
					options);
				return new UpdaterExecutionResult(
					delegatedExitCode,
					delegatedExitCode == SuccessExitCode
						? "最新版檢查與更新已完成。"
						: "新版 updater 未能完成更新，請查看錯誤訊息。");
			}
		}
		UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(currentUpdater, feed.Updater);

		if (preflightAction == OnlinePayloadUpdateAction.UseInstalled)
		{
			using UpdateInstallLock updateLock = UpdateInstallLock.Acquire(
				options.InstallRoot);
			RecoverInterruptedTransactionsWhileLocked(options.InstallRoot);
			UpdateManifest? lockedManifest =
				await ReadInstalledManifestAsync(
					options.InstallRoot,
					cancellationToken);
			OnlinePayloadUpdateAction lockedAction =
				OnlinePayloadUpdatePolicy.Evaluate(
					lockedManifest,
					availableManifest);

			if (lockedAction == OnlinePayloadUpdateAction.UseInstalled)
			{
				Console.WriteLine(
					$"AI Usage {lockedManifest!.Version} is already current.");
				return await RegisterAndRestartCurrentAsync(
					options,
					lockedManifest,
					cancellationToken);
			}
		}

		UpdateDownloadWorkspace workspace =
			UpdateDownloadWorkspace.Create(options.InstallRoot);
		string packagePath = workspace.GetFilePath(feed.Package.FileName);

		try
		{
			Console.WriteLine(
				$"Downloading AI Usage {feed.Package.Version}...");
			await onlineClient.DownloadArtifactAsync(
				feed.Package,
				packagePath,
				cancellationToken);
			return await ApplyPackageAsync(
				packagePath,
				availableManifest,
				options,
				enforceOnlinePolicy: true,
				cancellationToken);
		}
		finally
		{
			string? cleanupFailure = workspace.TryCleanup();

			if (cleanupFailure is not null)
			{
				Console.Error.WriteLine(
					$"Warning: update download cleanup failed: {cleanupFailure}");
			}
		}
	}

	private static async Task<UpdaterExecutionResult> ApplyPackageAsync(
		string packagePath,
		UpdateManifest manifest,
		UpdaterCommandLineOptions options,
		bool enforceOnlinePolicy,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(options);
		manifest.Validate();
		using UpdateInstallLock updateLock = UpdateInstallLock.Acquire(
			options.InstallRoot);
		RecoverInterruptedTransactionsWhileLocked(options.InstallRoot);

		if (enforceOnlinePolicy)
		{
			UpdateManifest? installedManifest =
				await ReadInstalledManifestAsync(
					options.InstallRoot,
					cancellationToken);
			OnlinePayloadUpdateAction lockedAction =
				OnlinePayloadUpdatePolicy.Evaluate(
					installedManifest,
					manifest);

			if (lockedAction == OnlinePayloadUpdateAction.UseInstalled)
			{
				return await RegisterAndRestartCurrentAsync(
					options,
					installedManifest!,
					cancellationToken);
			}
		}

		UpdateTransaction transaction = UpdateTransaction.Create(
			options.InstallRoot);

		try
		{
			RunningPayloadProcessGate processGate = CreateProcessGate();
			UpdatePackageStager stager = new();
			_ = await stager.StageAsync(
				packagePath,
				manifest,
				transaction,
				cancellationToken);
			transaction.TransitionTo(UpdateTransactionState.ShutdownRequested);
			await EnsureCurrentInstallationIsQuiescentAsync(
				transaction,
				processGate,
				manifest.PayloadGenerationId,
				cancellationToken);
			transaction.TransitionTo(UpdateTransactionState.AppExited);

			using (AppSingleInstanceMutexLease.Acquire(TimeSpan.FromSeconds(5)))
			{
				processGate.EnsureNoPayloadProcesses(
					transaction.CurrentDirectoryPath);
				new UpdateInstallationSwitcher().Switch(transaction);
			}
		}
		catch
		{
			TryMarkFailed(transaction);
			throw;
		}

		string executablePath = GetInstalledExecutablePath(options.InstallRoot);
		string? registrationWarning = await TryRegisterInstalledAppAsync(
			options,
			manifest,
			cancellationToken);
		Console.WriteLine(
			$"Update applied: {manifest.Version} ({transaction.TransactionId})");

		if (!options.ShouldRestart)
		{
			return new UpdaterExecutionResult(
				SuccessExitCode,
				AppendWarning(
					$"AI Usage {manifest.Version} 已更新完成。",
					registrationWarning));
		}

		try
		{
			StartApplication(executablePath);
			return new UpdaterExecutionResult(
				SuccessExitCode,
				AppendWarning(
					$"AI Usage {manifest.Version} 已更新並重新啟動。",
					registrationWarning));
		}
		catch (Exception exception) when (
			exception is Win32Exception or InvalidOperationException)
		{
			string message =
				"The update was applied, but AI Usage could not be restarted: " +
				exception.Message;
			Console.Error.WriteLine(message);
			return new UpdaterExecutionResult(RestartFailureExitCode, message);
		}
	}

	private static void RecoverInterruptedInstallation(string installRoot)
	{
		UpdateExecutionSafety.EnsureNotElevated();
		if (!Directory.Exists(installRoot))
		{
			return;
		}

		using UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(installRoot);
		RecoverInterruptedTransactionsWhileLocked(installRoot);
	}

	private static void RecoverInterruptedTransactionsWhileLocked(string installRoot)
	{
		IReadOnlyList<UpdateTransaction> pending = UpdateTransactionRecovery.FindPending(installRoot);
		if (pending.Count == 0)
		{
			return;
		}

		UpdateTransactionRecovery recovery = new();
		using AppSingleInstanceMutexLease? appLease = pending.Any(transaction => transaction.HasPendingSwitch)
			? AppSingleInstanceMutexLease.Acquire(TimeSpan.FromSeconds(5))
			: null;
		RunningPayloadProcessGate processGate = CreateProcessGate();
		foreach (UpdateTransaction transaction in pending)
		{
			if (transaction.HasPendingSwitch)
			{
				processGate.EnsureNoPayloadProcesses(transaction.CurrentDirectoryPath);
				processGate.EnsureNoPayloadProcesses(transaction.StagingDirectoryPath);
				processGate.EnsureNoPayloadProcesses(transaction.PreviousDirectoryPath);
			}

			recovery.Recover(transaction);
			Console.WriteLine($"Recovered interrupted update transaction: {transaction.TransactionId}");
		}
	}

	private static HttpClient CreateHttpClient()
	{
		HttpClientHandler handler = new()
		{
			AllowAutoRedirect = true,
			AutomaticDecompression = DecompressionMethods.None,
			CheckCertificateRevocationList = true,
			MaxAutomaticRedirections = 10,
			UseCookies = false
		};
		HttpClient client = new(handler)
		{
			Timeout = TimeSpan.FromMinutes(10)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd(
			"AiUsageDashboard-Updater/1.0");
		return client;
	}

	private static async Task EnsureCurrentInstallationIsQuiescentAsync(
		UpdateTransaction transaction,
		RunningPayloadProcessGate processGate,
		string firstInstallGenerationId,
		CancellationToken cancellationToken)
	{
		if (!Directory.Exists(transaction.CurrentDirectoryPath))
		{
			await processGate.EnsureQuiescentAsync(
				transaction.CurrentDirectoryPath,
				firstInstallGenerationId,
				cancellationToken);
			return;
		}

		UpdateManifest installedManifest = await UpdateManifest.ReadAsync(
			Path.Combine(
				transaction.CurrentDirectoryPath,
				UpdateManifest.InstalledFileName),
			cancellationToken);
		await processGate.EnsureQuiescentAsync(
			transaction.CurrentDirectoryPath,
			installedManifest.PayloadGenerationId,
			cancellationToken);
	}

	private static RunningPayloadProcessGate CreateProcessGate()
	{
		return new RunningPayloadProcessGate(
			new SystemRunningPayloadProcessProbe(),
			new NamedPipeUpdateShutdownProtocolClient(
				UpdateShutdownChannel.CreateCurrentUserPipeName()),
			TimeSpan.FromSeconds(5),
			TimeSpan.FromMinutes(5));
	}

	private static string GetInstalledExecutablePath(string installRoot)
	{
		return ManagedInstallationPaths.GetInstalledAppExecutable(installRoot);
	}

	private static async Task<UpdateManifest?> ReadInstalledManifestAsync(
		string installRoot,
		CancellationToken cancellationToken)
	{
		string currentDirectory =
			ManagedInstallationPaths.GetCurrentDirectory(installRoot);

		if (!Directory.Exists(currentDirectory))
		{
			return null;
		}

		return await UpdateManifest.ReadAsync(
			Path.Combine(currentDirectory, UpdateManifest.InstalledFileName),
			cancellationToken);
	}

	private static async Task<int> RunDelegatedUpdaterAsync(
		string executablePath,
		UpdaterCommandLineOptions options)
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = executablePath,
			WorkingDirectory = Path.GetDirectoryName(executablePath) ??
				throw new InvalidOperationException(
					"The downloaded updater has no working directory."),
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("update-online");
		startInfo.ArgumentList.Add("--feed-url");
		startInfo.ArgumentList.Add(
			options.FeedUri?.AbsoluteUri ?? throw new InvalidOperationException(
				"The delegated updater has no feed URL."));
		startInfo.ArgumentList.Add("--install-root");
		startInfo.ArgumentList.Add(options.InstallRoot);
		startInfo.ArgumentList.Add("--skip-updater-refresh");
		if (options.ShouldPromptForLicenses)
		{
			startInfo.ArgumentList.Add("--prompt-for-licenses");
		}

		if (!options.ShouldRestart)
		{
			startInfo.ArgumentList.Add("--no-restart");
		}

		using Process process = Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"Windows did not start the downloaded updater.");
		await process.WaitForExitAsync();
		return process.ExitCode;
	}

	private static async Task<UpdaterExecutionResult>
		RegisterAndRestartCurrentAsync(
			UpdaterCommandLineOptions options,
			UpdateManifest installedManifest,
			CancellationToken cancellationToken)
	{
		string? registrationWarning = await TryRegisterInstalledAppAsync(
			options,
			installedManifest,
			cancellationToken);

		if (!options.ShouldRestart)
		{
			return new UpdaterExecutionResult(
				SuccessExitCode,
				AppendWarning(
					"AI Usage 已是最新版。",
					registrationWarning));
		}

		try
		{
			StartApplication(GetInstalledExecutablePath(options.InstallRoot));
			return new UpdaterExecutionResult(
				SuccessExitCode,
				AppendWarning(
					"AI Usage 已是最新版，並已啟動。",
					registrationWarning));
		}
		catch (Exception exception) when (
			exception is Win32Exception or InvalidOperationException)
		{
			string message =
				"AI Usage is current, but it could not be started: " +
				exception.Message;
			Console.Error.WriteLine(message);
			return new UpdaterExecutionResult(RestartFailureExitCode, message);
		}
	}

	private static async Task<UpdaterExecutionResult> UninstallAsync(
		UpdaterCommandLineOptions options,
		CancellationToken cancellationToken)
	{
		if (!options.IsUninstallConfirmed)
		{
			throw new InvalidOperationException(
				"The uninstall command was not confirmed.");
		}

		UpdateExecutionSafety.EnsureNotElevated();
		string runningUpdaterPath = Environment.ProcessPath ??
			throw new InvalidOperationException(
				"The running updater executable path is unavailable.");
		ManagedInstallationUninstaller uninstaller = new(
			CreateProcessGate(),
			new CurrentUserUninstallRegistryStore(),
			new CurrentUserLogonStartupRegistryStore(),
			new MaintenanceUpdaterCleanup());
		ManagedInstallationUninstallResult result =
			await uninstaller.UninstallAsync(
				runningUpdaterPath,
				options.InstallRoot,
				options.MaintenanceRoot,
				options.UserDataRoot,
				options.ShouldRegisterInstalledApp,
				cancellationToken);
		string message = result.WasAlreadyAbsent
			? "AI Usage 已經解除安裝。"
			: result.InstallRootRemoved
				? "AI Usage 已解除安裝；帳號與設定已保留。"
				: $"AI Usage 程式已移除；未知檔案保留在 " +
					$"{result.PreservedInstallRoot}。";

		if (result.Warning is not null)
		{
			Console.Error.WriteLine(
				$"Warning: {result.Warning}");
			message = AppendWarning(message, result.Warning);
		}

		Console.WriteLine(message);
		return new UpdaterExecutionResult(SuccessExitCode, message);
	}

	private static async Task<string?> TryRegisterInstalledAppAsync(
		UpdaterCommandLineOptions options,
		UpdateManifest installedManifest,
		CancellationToken cancellationToken)
	{
		if (!options.ShouldRegisterInstalledApp)
		{
			return null;
		}

		try
		{
			string runningUpdaterPath = Environment.ProcessPath ??
				throw new InvalidOperationException(
					"The running updater executable path is unavailable.");
			ManagedInstallationRegistrar registrar = new(
				new CurrentUserUninstallRegistryStore());
			await registrar.EnsureRegisteredAsync(
				runningUpdaterPath,
				options.InstallRoot,
				options.MaintenanceRoot,
				options.UserDataRoot,
				installedManifest,
				cancellationToken);
			return null;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or
				SecurityException or InvalidDataException or
				InvalidOperationException or ArgumentException)
		{
			string warning =
				"App 已更新，但無法登錄 Windows 已安裝的應用程式：" +
				exception.Message;
			Console.Error.WriteLine($"Warning: {warning}");
			return warning;
		}
	}

	private static string AppendWarning(string message, string? warning)
	{
		return warning is null ? message : $"{message} {warning}";
	}

	private static void StartApplication(string executablePath)
	{
		if (!File.Exists(executablePath))
		{
			throw new InvalidOperationException(
				"The installed application executable was not found.");
		}

		_ = Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			WorkingDirectory = Path.GetDirectoryName(executablePath) ??
				throw new InvalidOperationException(
					"The installed application has no working directory."),
			UseShellExecute = true
		}) ?? throw new InvalidOperationException(
			"Windows did not start the installed application.");
	}

	private static void NotifyFailureIfRequested(
		bool shouldNotifyUser,
		string message)
	{
		if (shouldNotifyUser)
		{
			UpdaterUserNotifier.ShowError(message);
		}
	}

	private static void TryMarkFailed(UpdateTransaction transaction)
	{
		try
		{
			if (transaction.State is UpdateTransactionState.Prepared or
				UpdateTransactionState.Staged or
				UpdateTransactionState.ShutdownRequested or
				UpdateTransactionState.AppExited or
				UpdateTransactionState.Switching)
			{
				transaction.TransitionTo(UpdateTransactionState.Failed);
			}
		}
		catch (Exception exception) when (
			exception is IOException or InvalidOperationException or
				UnauthorizedAccessException)
		{
			// 原始失敗比 receipt 更新失敗更重要。
		}
	}

	private sealed record UpdaterExecutionResult(int ExitCode, string UserMessage);
}
