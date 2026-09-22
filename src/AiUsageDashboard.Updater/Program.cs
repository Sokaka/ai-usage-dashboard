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
	private const int MaintenanceUpdaterIncompleteExitCode = 4;
	private const int RestartFailureExitCode = 3;
	private const int SuccessExitCode = 0;

	public static async Task<int> Main(string[] arguments)
	{
		bool shouldNotifyUser = arguments.Length == 0;

		try
		{
			if (MaintenanceUpdaterPromotionCommandLine.IsCommand(arguments))
			{
				UpdateExecutionSafety.EnsureNotElevated();
				MaintenanceUpdaterPromotionOptions promotionOptions =
					MaintenanceUpdaterPromotionCommandLine.Parse(arguments);
				await new MaintenanceUpdaterPromotion(
					new SystemExactProcessExitWaiter()).PromoteAndRecordAsync(
						promotionOptions,
						Environment.ProcessPath ??
							throw new InvalidOperationException(
								"The running updater executable path is unavailable."),
						CancellationToken.None);
				return SuccessExitCode;
			}

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
		CurrentUpdaterIdentity currentUpdater =
			await CurrentUpdaterIdentity.ReadAsync(
				Environment.ProcessPath ?? throw new InvalidOperationException(
					"The running updater executable path is unavailable."),
				cancellationToken);
		DelegatedMaintenanceUpdaterPromotionCoordinator promotionCoordinator = new(
			new SystemProcessLineageProbe(),
			new MaintenanceUpdaterPromotionLauncher());
		DelegatedMaintenanceUpdaterPromotionContext? delegatedPromotionContext = null;
		string? delegatedPromotionWarning = null;

		try
		{
			delegatedPromotionContext = await promotionCoordinator.CaptureAsync(
				currentUpdater,
				options,
				cancellationToken);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or
				SecurityException or InvalidDataException or
				InvalidOperationException or ArgumentException or
				Win32Exception)
		{
			delegatedPromotionWarning =
				"無法確認 delegated maintenance updater 的 parent 身分：" +
				exception.Message;
			Console.Error.WriteLine($"Warning: {delegatedPromotionWarning}");
		}

		string? pendingPromotionWarning =
			await TryRetryPendingMaintenanceUpdaterPromotionAsync(
			currentUpdater,
			options,
			cancellationToken);

		using HttpClient httpClient = CreateHttpClient();
		SignedUpdateFeedClient feedClient = new(
			httpClient,
			UpdaterBuildDefaults.LoadTrustedKeys());
		OnlineUpdateClient onlineClient = new(httpClient);
		ResolvedUpdateReleaseFeed resolvedFeed =
			await feedClient.FetchFeedAsync(
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
				string delegatedMessage = delegatedExitCode == SuccessExitCode
					? "最新版檢查與更新已完成。"
					: "新版 updater 未能完成更新，請查看錯誤訊息。";
				UpdaterExecutionResult delegatedResult = new(
					delegatedExitCode,
					delegatedMessage);
				return ApplyMaintenanceUpdaterWarning(
					delegatedResult,
					delegatedExitCode == SuccessExitCode
						? null
						: pendingPromotionWarning);
			}
		}
		UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(currentUpdater, feed.Updater);

		if (preflightAction == OnlinePayloadUpdateAction.UseInstalled)
		{
			UpdaterExecutionResult? currentResult = null;
			using (UpdateInstallLock updateLock = UpdateInstallLock.Acquire(
				options.InstallRoot))
			{
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
					currentResult = await RegisterAndRestartCurrentAsync(
						options,
						lockedManifest,
						cancellationToken);
				}
			}

			if (currentResult is not null)
			{
				return await CompleteDelegatedMaintenanceUpdaterPromotionAsync(
					currentResult,
					delegatedPromotionContext,
					delegatedPromotionWarning,
					pendingPromotionWarning,
					promotionCoordinator,
					currentUpdater,
					feed.Updater,
					options,
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
			UpdaterExecutionResult updateResult = await ApplyPackageAsync(
				packagePath,
				availableManifest,
				options,
				enforceOnlinePolicy: true,
				cancellationToken);
			return await CompleteDelegatedMaintenanceUpdaterPromotionAsync(
				updateResult,
				delegatedPromotionContext,
				delegatedPromotionWarning,
				pendingPromotionWarning,
				promotionCoordinator,
				currentUpdater,
				feed.Updater,
				options,
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
		CompleteDelegatedMaintenanceUpdaterPromotionAsync(
			UpdaterExecutionResult result,
			DelegatedMaintenanceUpdaterPromotionContext? context,
			string? preparationWarning,
			string? pendingPromotionWarning,
			DelegatedMaintenanceUpdaterPromotionCoordinator coordinator,
			CurrentUpdaterIdentity currentUpdater,
			UpdateReleaseArtifact availableUpdater,
			UpdaterCommandLineOptions options,
			CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(coordinator);
		ArgumentNullException.ThrowIfNull(currentUpdater);
		ArgumentNullException.ThrowIfNull(availableUpdater);
		ArgumentNullException.ThrowIfNull(options);
		string? warning = pendingPromotionWarning;

		if (result.ExitCode is not SuccessExitCode and not RestartFailureExitCode)
		{
			return ApplyMaintenanceUpdaterWarning(
				result,
				CombineWarnings(warning, preparationWarning));
		}

		if (context is null)
		{
			if (!options.ShouldRefreshUpdater &&
				options.ShouldRegisterInstalledApp)
			{
				try
				{
					bool isCanonicalCurrent =
						await coordinator.IsCanonicalUpdaterCurrentAsync(
							currentUpdater,
							availableUpdater,
							options,
							cancellationToken);
					preparationWarning = isCanonicalCurrent
						? null
						: preparationWarning ??
							"App 已更新，但 canonical maintenance updater 仍不是 " +
							"signed feed 指定版本。";
				}
				catch (Exception exception) when (
					exception is IOException or UnauthorizedAccessException or
						SecurityException or InvalidDataException or
						InvalidOperationException or ArgumentException or
						Win32Exception)
				{
					preparationWarning = CombineWarnings(
						preparationWarning,
						"App 已更新，但無法驗證 canonical maintenance updater：" +
							exception.Message);
				}
			}

			warning = CombineWarnings(warning, preparationWarning);
			return ApplyMaintenanceUpdaterWarning(result, warning);
		}

		try
		{
			DelegatedMaintenanceUpdaterPromotionState promotionState =
				await coordinator.ScheduleAsync(
				context,
				currentUpdater,
				availableUpdater,
				options,
				cancellationToken);

			switch (promotionState)
			{
				case DelegatedMaintenanceUpdaterPromotionState.CanonicalCurrent:
					Console.WriteLine(
						"Canonical maintenance updater is already current.");
					break;
				case DelegatedMaintenanceUpdaterPromotionState.PendingForExactParent:
					string generationPath = ManagedInstallationPaths
						.GetMaintenanceUpdaterGeneration(
							options.MaintenanceRoot,
							availableUpdater.Sha256);
					Console.WriteLine(
						"Scheduled delegated maintenance updater promotion from " +
						$"'{generationPath}' after process " +
						$"{context.ParentIdentity.ProcessId} exits.");
					break;
				default:
					throw new InvalidOperationException(
						"Unsupported maintenance updater promotion state " +
							$"'{promotionState}'.");
			}
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or
				SecurityException or InvalidDataException or
				InvalidOperationException or ArgumentException or
				Win32Exception)
		{
			warning = CombineWarnings(
				warning,
				"App 已更新，但無法安排 delegated maintenance updater 提升：" +
					exception.Message);
			Console.Error.WriteLine(
				$"Warning: {warning ?? "Maintenance updater promotion failed."}");
		}

		return ApplyMaintenanceUpdaterWarning(result, warning);
	}

	private static async Task<string?>
		TryRetryPendingMaintenanceUpdaterPromotionAsync(
		CurrentUpdaterIdentity currentUpdater,
		UpdaterCommandLineOptions options,
		CancellationToken cancellationToken)
	{
		if (!options.ShouldRegisterInstalledApp)
		{
			return null;
		}

		string canonicalPath = ManagedInstallationPaths.GetMaintenanceUpdater(
			options.MaintenanceRoot);
		if (!string.Equals(
				currentUpdater.ExecutablePath,
				canonicalPath,
				StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		MaintenanceUpdaterPromotionReceiptStore receiptStore = new(
			options.MaintenanceRoot);

		try
		{
			MaintenanceUpdaterPromotionReceipt? receipt =
				await receiptStore.LoadAsync(cancellationToken);

			if (receipt is null)
			{
				return null;
			}

			MaintenanceUpdaterPromotionOptions recordedOptions = new(
				options.InstallRoot,
				options.MaintenanceRoot,
				receipt.SourceSha256,
				receipt.ExpectedCanonicalSha256,
				new ExactProcessIdentity(
					receipt.ParentProcessId,
					receipt.ParentProcessStartTimeUtcTicks));

			if (string.Equals(
					currentUpdater.Sha256,
					receipt.SourceSha256,
					StringComparison.Ordinal))
			{
				await receiptStore.DeleteIfMatchesAsync(
					recordedOptions,
					CancellationToken.None);
				return null;
			}

			using Process currentProcess = Process.GetCurrentProcess();
			MaintenanceUpdaterPromotionOptions retryOptions = new(
				options.InstallRoot,
				options.MaintenanceRoot,
				receipt.SourceSha256,
				receipt.ExpectedCanonicalSha256,
				new ExactProcessIdentity(
					currentProcess.Id,
					currentProcess.StartTime.ToUniversalTime().Ticks));

			if (!string.Equals(
					currentUpdater.Sha256,
					receipt.ExpectedCanonicalSha256,
					StringComparison.Ordinal))
			{
				InvalidDataException changedCanonicalException = new(
					$"Canonical maintenance updater '{canonicalPath}' no longer " +
					"matches the generation recorded for pending promotion.");
				await receiptStore.SaveFailureIfCurrentAsync(
					recordedOptions,
					changedCanonicalException,
					CancellationToken.None);
				string warning = changedCanonicalException.Message;
				Console.Error.WriteLine($"Warning: {warning}");
				return warning;
			}

			bool wasLaunched = await new MaintenanceUpdaterPromotionLauncher()
				.LaunchRetryAsync(
					retryOptions,
					receipt,
					cancellationToken);

			if (wasLaunched)
			{
				Console.WriteLine(
					"Retrying the pending maintenance updater promotion after " +
					$"process {retryOptions.ParentIdentity.ProcessId} exits.");
			}

			return null;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or
				SecurityException or InvalidDataException or
				InvalidOperationException or ArgumentException or
				Win32Exception)
		{
			string warning =
				"Pending maintenance updater promotion could not be " +
				$"retried: {exception.Message}";
			Console.Error.WriteLine($"Warning: {warning}");
			return warning;
		}
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
			new MaintenanceUpdaterCleanup(),
			new ManagedStartMenuShortcut());
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
				new CurrentUserUninstallRegistryStore(),
				new ManagedStartMenuShortcut());
			string? shortcutWarning = await registrar.EnsureRegisteredAsync(
				runningUpdaterPath,
				options.InstallRoot,
				options.MaintenanceRoot,
				options.UserDataRoot,
				installedManifest,
				cancellationToken);

			if (shortcutWarning is not null)
			{
				Console.Error.WriteLine($"Warning: {shortcutWarning}");
			}

			return shortcutWarning;
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

	private static UpdaterExecutionResult ApplyMaintenanceUpdaterWarning(
		UpdaterExecutionResult result,
		string? warning)
	{
		ArgumentNullException.ThrowIfNull(result);

		if (warning is null)
		{
			return result;
		}

		return result with
		{
			ExitCode = result.ExitCode == SuccessExitCode
				? MaintenanceUpdaterIncompleteExitCode
				: result.ExitCode,
			UserMessage = AppendWarning(result.UserMessage, warning)
		};
	}

	private static string? CombineWarnings(string? first, string? second)
	{
		if (first is null)
		{
			return second;
		}

		return second is null ? first : $"{first} {second}";
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
