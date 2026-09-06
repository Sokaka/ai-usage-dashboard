using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityExecutableSearchDriveType : uint
{
	Unknown = 0,
	NoRootDirectory = 1,
	Removable = 2,
	Fixed = 3,
	Remote = 4,
	CdRom = 5,
	RamDisk = 6
}

public sealed class AntigravityMachineSetupService :
	IAntigravityMachineSetupService
{
	private abstract record SupportedBuild(
		string CliVersion,
		string ExecutablePath,
		AntigravityMachineSetupSourceKind SourceKind);

	private sealed record ReviewedConPtySupportedBuild(
		ReviewedConPtyEligibilityReceipt Eligibility,
		AntigravityCliFingerprint Fingerprint)
		: SupportedBuild(
			Fingerprint.CliVersion,
			Fingerprint.AbsolutePath,
			AntigravityMachineSetupSourceKind.ReviewedConPty)
	{
		internal AntigravityReviewedPackageContract Contract =>
			Eligibility.Contract;
	}

	private sealed record ReviewedConPtyEligibilityReceipt(
		AntigravityReviewedPackageContract Contract,
		string ProfilePath,
		string ProfileBytesSha256,
		string CaptureContractFingerprint,
		string SettingsBaselineFingerprint,
		string LocalBinding);

	private sealed record OfficialPrintSupportedBuild(
		string Version,
		string Path)
		: SupportedBuild(
			Version,
			Path,
			AntigravityMachineSetupSourceKind.OfficialPrint);

	internal sealed record UserEnvironmentChange(
		string Name,
		string? Value);

	private sealed class ExecutableDiscoveryOperation
	{
		internal CancellationTokenSource CancellationSource { get; }

		internal Task<string[]> Task { get; }

		internal ExecutableDiscoveryOperation(
			CancellationTokenSource cancellationSource,
			Task<string[]> task)
		{
			CancellationSource = cancellationSource;
			Task = task;
		}

		internal void TryCancel()
		{
			try
			{
				CancellationSource.Cancel();
			}
			catch (ObjectDisposedException)
			{
				// Completion won the race and already released the source.
			}
		}
	}

	private sealed class CrossProcessSourceMutationLease : IDisposable
	{
		private Semaphore? _semaphore;

		internal CrossProcessSourceMutationLease(Semaphore semaphore)
		{
			_semaphore = semaphore;
		}

		public void Dispose()
		{
			Semaphore? semaphore = Interlocked.Exchange(
				ref _semaphore,
				null);

			if (semaphore is null)
			{
				return;
			}

			try
			{
				semaphore.Release();
			}
			finally
			{
				semaphore.Dispose();
			}
		}
	}

	private sealed class SetupFailureException : Exception
	{
		internal AntigravityMachineSetupFailureKind FailureKind { get; }

		internal SetupFailureException(
			AntigravityMachineSetupFailureKind failureKind)
			: base("The AGY machine setup failed closed.")
		{
			if ((failureKind == AntigravityMachineSetupFailureKind.None) ||
				!Enum.IsDefined(failureKind))
			{
				throw new ArgumentOutOfRangeException(nameof(failureKind));
			}

			FailureKind = failureKind;
		}
	}

	private const string AutoUpdateEnvironmentVariable =
		"AGY_CLI_DISABLE_AUTO_UPDATE";
	private const string ProfileEnvironmentVariable =
		"AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE";
	private const string SourceMutationSemaphoreName =
		@"Local\AiUsageDashboard.AntigravityMachineSetup.SourceMutation.v1";
	private const int MaximumExistingProfileBytes = 1024 * 1024;
	private const int MaximumPathEntryCount = 256;
	private const int MaximumSavedOutputBytes = 1024 * 1024;
	private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan ExecutableDiscoveryTimeout =
		TimeSpan.FromSeconds(10);
	private static readonly TimeSpan SourceMutationWaitTimeout =
		TimeSpan.FromSeconds(10);
	private static readonly string[] CopiedEnvironmentVariableNames =
	{
		"APPDATA",
		"COLORTERM",
		"HOME",
		"HOMEDRIVE",
		"HOMEPATH",
		"LANG",
		"LOCALAPPDATA",
		"ProgramData",
		"ProgramFiles",
		"ProgramFiles(x86)",
		"ProgramW6432",
		"SystemRoot",
		"TEMP",
		"TERM",
		"TMP",
		"USERPROFILE",
		"WINDIR"
	};
	private static readonly object ExecutableDiscoverySyncRoot = new();
	private static ExecutableDiscoveryOperation? _activeExecutableDiscovery;
	private static readonly TimeSpan PollInterval =
		TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan StableScreenDuration =
		TimeSpan.FromSeconds(2);
	private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(30);
	private readonly Func<
		string,
		CancellationToken,
		Task<AntigravityOfficialPrintCapabilityValidationResult>>
		_validateOfficialPrintCapabilityAsync;
	private readonly IAntigravityOfficialPrintUsageClient
		_officialPrintUsageClient;
	private readonly IAntigravityProductionUsageClient
		_reviewedConPtyUsageClient;
	private readonly Func<string, EnvironmentVariableTarget, string?>
		_getEnvironmentVariable;
	private readonly Action<string, string?, EnvironmentVariableTarget>
		_setEnvironmentVariable;
	private readonly Func<Environment.SpecialFolder, string> _getFolderPath;
	private readonly Func<
		CancellationToken,
		Task<IReadOnlyList<string>>> _discoverExecutableCandidatesAsync;
	private readonly Func<
		string,
		AntigravityReviewedPackageContract,
		CancellationToken,
		Task<AntigravityCliCapabilityValidationResult>>
		_validateReviewedConPtyCapabilityAsync;
	private readonly Func<
		AntigravityLiveR0Profile,
		CancellationToken,
		Task<string>> _captureReviewedConPtySettingsBaselineAsync;
	private readonly Func<
		AntigravityLiveR0Profile,
		CancellationToken,
		Task<AntigravityLiveR0Report>>
		_runReviewedConPtyPromptCalibrationAsync;

	public AntigravityMachineSetupService()
		: this(CreateDefaultOfficialPrintUsageClient())
	{
	}

	public AntigravityMachineSetupService(
		IAntigravityOfficialPrintUsageClient officialPrintUsageClient)
		: this(
			CreateOfficialPrintCapabilityValidator(officialPrintUsageClient),
			officialPrintUsageClient,
			Environment.GetEnvironmentVariable,
			Environment.SetEnvironmentVariable,
			discoverExecutableCandidatesAsync: null)
	{
	}

	private static Func<
		string,
		CancellationToken,
		Task<AntigravityOfficialPrintCapabilityValidationResult>>
		CreateOfficialPrintCapabilityValidator(
			IAntigravityOfficialPrintUsageClient officialPrintUsageClient)
	{
		ArgumentNullException.ThrowIfNull(officialPrintUsageClient);

		if (officialPrintUsageClient is
			IAntigravityOfficialPrintCapabilityValidationClient gatedClient)
		{
			return gatedClient.ValidateCapabilityAsync;
		}

		return (executablePath, cancellationToken) =>
			new AntigravityOfficialPrintCapabilityValidator().ValidateAsync(
				executablePath,
				cancellationToken);
	}

	private static IAntigravityOfficialPrintUsageClient
		CreateDefaultOfficialPrintUsageClient()
	{
		return new AntigravityOfficialPrintUsageClient(
			new JsonAntigravityOfficialPrintSafetyStateStore(
				AntigravityOfficialPrintSafetyStatePaths.GetDefaultFilePath()));
	}

	internal AntigravityMachineSetupService(
		Func<
			string,
			CancellationToken,
			Task<AntigravityOfficialPrintCapabilityValidationResult>>
			validateOfficialPrintCapabilityAsync,
		IAntigravityOfficialPrintUsageClient officialPrintUsageClient,
		Func<string, EnvironmentVariableTarget, string?>
			getEnvironmentVariable,
		Action<string, string?, EnvironmentVariableTarget>
			setEnvironmentVariable,
		Func<CancellationToken, Task<IReadOnlyList<string>>>?
			discoverExecutableCandidatesAsync = null,
		Func<
			string,
			AntigravityReviewedPackageContract,
			CancellationToken,
			Task<AntigravityCliCapabilityValidationResult>>?
			validateReviewedConPtyCapabilityAsync = null,
		Func<
			AntigravityLiveR0Profile,
			CancellationToken,
			Task<string>>?
			captureReviewedConPtySettingsBaselineAsync = null,
		IAntigravityProductionUsageClient?
			reviewedConPtyUsageClient = null,
		Func<Environment.SpecialFolder, string>? getFolderPath = null,
		Func<
			AntigravityLiveR0Profile,
			CancellationToken,
			Task<AntigravityLiveR0Report>>?
			runReviewedConPtyPromptCalibrationAsync = null)
	{
		_validateOfficialPrintCapabilityAsync =
			validateOfficialPrintCapabilityAsync ??
			throw new ArgumentNullException(
				nameof(validateOfficialPrintCapabilityAsync));
		_officialPrintUsageClient = officialPrintUsageClient ??
			throw new ArgumentNullException(nameof(officialPrintUsageClient));
		_reviewedConPtyUsageClient = reviewedConPtyUsageClient ??
			new AntigravityProductionUsageClient();
		_getEnvironmentVariable = getEnvironmentVariable ??
			throw new ArgumentNullException(nameof(getEnvironmentVariable));
		_setEnvironmentVariable = setEnvironmentVariable ??
			throw new ArgumentNullException(nameof(setEnvironmentVariable));
		_getFolderPath = getFolderPath ?? Environment.GetFolderPath;
		_discoverExecutableCandidatesAsync =
			discoverExecutableCandidatesAsync ??
			DiscoverCurrentExecutableCandidatesAsync;
		_validateReviewedConPtyCapabilityAsync =
			validateReviewedConPtyCapabilityAsync ??
			ValidateReviewedConPtyCapabilityAsync;
		_captureReviewedConPtySettingsBaselineAsync =
			captureReviewedConPtySettingsBaselineAsync ??
			CaptureReviewedConPtySettingsBaselineAsync;
		_runReviewedConPtyPromptCalibrationAsync =
			runReviewedConPtyPromptCalibrationAsync ??
			RunReviewedConPtyPromptCalibrationAsync;
	}

	private static Task<AntigravityCliCapabilityValidationResult>
		ValidateReviewedConPtyCapabilityAsync(
			string executablePath,
			AntigravityReviewedPackageContract contract,
			CancellationToken cancellationToken)
	{
		AntigravityReviewedPackageExecutable reviewed = contract.Executable;
		AntigravityCliFingerprint expected = new(
			executablePath,
			reviewed.CliVersion,
			reviewed.FileVersion,
			reviewed.ProductVersion,
			reviewed.Sha256,
			reviewed.WinVerifyTrustStatus,
			reviewed.SignerSubject,
			reviewed.SignerThumbprint);
		AntigravityCliCapabilityValidator validator = new(new[] { expected });
		return validator.ValidateAsync(executablePath, cancellationToken);
	}

	private static Task<string> CaptureReviewedConPtySettingsBaselineAsync(
		AntigravityLiveR0Profile profile,
		CancellationToken cancellationToken)
	{
		return new AntigravityLiveR0Runner()
			.CaptureR1PrivateCalibrationSettingsBaselineFingerprintAsync(
				profile,
				cancellationToken);
	}

	private static Task<AntigravityLiveR0Report>
		RunReviewedConPtyPromptCalibrationAsync(
			AntigravityLiveR0Profile profile,
			CancellationToken cancellationToken)
	{
		return new AntigravityLiveR0Runner().RunAsync(
			AntigravityLiveR0Mode.CalibratePrompt,
			profile,
			new AntigravityLiveR0Consent(true),
			cancellationToken);
	}

	private static async Task<IReadOnlyList<string>>
		DiscoverCurrentExecutableCandidatesAsync(
			CancellationToken cancellationToken)
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		return await DiscoverExecutableCandidatesAsync(
				localApplicationData,
				Environment.GetEnvironmentVariable("PATH"),
				File.Exists,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task<AntigravityMachineSetupPreparationResult> PrepareAsync(
		AntigravityMachineSetupConsent consent,
		IProgress<AntigravityMachineSetupProgress>? progress,
		CancellationToken cancellationToken)
	{
		if ((consent is null) ||
			!consent.IsLiveCaptureApproved ||
			!consent.HasConfirmedCommandReadyPrompt)
		{
			return Failure(
				AntigravityMachineSetupFailureKind.ConsentRequired);
		}

		cancellationToken.ThrowIfCancellationRequested();
		progress?.Report(new AntigravityMachineSetupProgress(
			AntigravityMachineSetupStage.LoadingReviewedContract));
		SupportedBuild? supportedBuild;

		try
		{
			supportedBuild = await FindSupportedBuildAsync(
				progress,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (SetupFailureException exception)
		{
			return Failure(exception.FailureKind);
		}
		catch
		{
			return Failure(
				AntigravityMachineSetupFailureKind.UnexpectedFailure);
		}

		if (supportedBuild is null)
		{
			return Failure(
				AntigravityMachineSetupFailureKind.UnsupportedBuild);
		}

		return supportedBuild switch
		{
			OfficialPrintSupportedBuild officialPrintBuild =>
				await PrepareOfficialPrintBuildAsync(
					officialPrintBuild,
					progress,
					cancellationToken),
			ReviewedConPtySupportedBuild reviewedConPtyBuild =>
				await PrepareReviewedConPtyBuildAsync(
					reviewedConPtyBuild,
					progress,
					cancellationToken),
			_ => Failure(
				AntigravityMachineSetupFailureKind.UnexpectedFailure)
		};
	}

	private async Task ApproveReviewedConPtyAsync(
		AntigravityMachineSetupPrivateTransaction transaction,
		ReviewedConPtyEligibilityReceipt expectedEligibility,
		CancellationToken cancellationToken)
	{
		using CrossProcessSourceMutationLease mutationLease =
			await AcquireSourceMutationLeaseAsync(cancellationToken)
				.ConfigureAwait(false);

		if (!string.IsNullOrWhiteSpace(_getEnvironmentVariable(
				AntigravityMachineSetupEnvironmentVariables.OfficialExecutable,
				EnvironmentVariableTarget.User)) ||
			!string.IsNullOrWhiteSpace(_getEnvironmentVariable(
				AntigravityMachineSetupEnvironmentVariables.OfficialExecutable,
				EnvironmentVariableTarget.Process)))
		{
			throw new InvalidOperationException(
				"The AGY source changed before legacy approval.");
		}

		ReviewedConPtyEligibilityReceipt? currentEligibility =
			await TryLoadExistingReviewedConPtyEligibilityAsync(
				cancellationToken).ConfigureAwait(false);

		if ((currentEligibility is null) ||
			!IsSameReviewedConPtyEligibility(
				expectedEligibility,
				currentEligibility))
		{
			throw new InvalidOperationException(
				"The approved AGY legacy source changed before approval.");
		}

		ApplyUserEnvironmentChanges(
			new[]
			{
				new UserEnvironmentChange(
					ProfileEnvironmentVariable,
					transaction.ProfilePath),
				new UserEnvironmentChange(
					AntigravityMachineSetupEnvironmentVariables
						.OfficialExecutable,
					Value: null)
			},
			_getEnvironmentVariable,
			_setEnvironmentVariable,
			cancellationToken,
			transaction.MarkApproved);
	}

	private async Task ApproveOfficialPrintAsync(
		string executablePath,
		string cliVersion,
		CancellationToken cancellationToken)
	{
		using CrossProcessSourceMutationLease mutationLease =
			await AcquireSourceMutationLeaseAsync(cancellationToken)
				.ConfigureAwait(false);
		AntigravityOfficialPrintCapabilityValidationResult validation =
			await _validateOfficialPrintCapabilityAsync(
				executablePath,
				cancellationToken) ??
			throw new InvalidOperationException(
				"The official AGY capability validator returned no result.");

		using (validation)
		{
			AntigravityExecutableLease? lease = validation.ExecutableLease;

			if (!validation.IsSupported ||
				string.IsNullOrWhiteSpace(validation.CliVersion) ||
				!string.Equals(
					validation.CliVersion,
					cliVersion,
					StringComparison.Ordinal) ||
				(lease is null) ||
				lease.IsDisposed ||
				!string.Equals(
					lease.AbsolutePath,
					executablePath,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					"The official AGY executable changed before approval.");
			}

			ApplyUserEnvironmentChanges(
				new[]
				{
					new UserEnvironmentChange(
						AntigravityMachineSetupEnvironmentVariables
							.OfficialExecutable,
						executablePath)
				},
				_getEnvironmentVariable,
				_setEnvironmentVariable,
				cancellationToken);
		}
	}

	private static async Task<CrossProcessSourceMutationLease>
		AcquireSourceMutationLeaseAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Semaphore? semaphore = null;
		bool isAcquired = false;

		try
		{
			semaphore = new Semaphore(
				initialCount: 1,
				maximumCount: 1,
				name: SourceMutationSemaphoreName);
			WaitHandle[] waitHandles =
			{
				semaphore,
				cancellationToken.WaitHandle
			};
			int signaledIndex = await Task.Run(
				() => WaitHandle.WaitAny(
					waitHandles,
					SourceMutationWaitTimeout),
				CancellationToken.None).ConfigureAwait(false);

			if (signaledIndex == 1)
			{
				throw new OperationCanceledException(cancellationToken);
			}

			if (signaledIndex == WaitHandle.WaitTimeout)
			{
				throw new TimeoutException(
					"The AGY source mutation gate remained busy.");
			}

			isAcquired = true;
			cancellationToken.ThrowIfCancellationRequested();
			CrossProcessSourceMutationLease lease = new(semaphore);
			semaphore = null;
			return lease;
		}
		finally
		{
			if (semaphore is not null)
			{
				if (isAcquired)
				{
					semaphore.Release();
				}

				semaphore.Dispose();
			}
		}
	}

	internal static void ApplyUserEnvironmentChanges(
		IReadOnlyList<UserEnvironmentChange> changes,
		Func<string, EnvironmentVariableTarget, string?>
			getEnvironmentVariable,
		Action<string, string?, EnvironmentVariableTarget>
			setEnvironmentVariable,
		CancellationToken cancellationToken,
		Action? commit = null)
	{
		ArgumentNullException.ThrowIfNull(changes);
		ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
		ArgumentNullException.ThrowIfNull(setEnvironmentVariable);
		cancellationToken.ThrowIfCancellationRequested();

		if ((changes.Count == 0) ||
			changes.Any(change =>
				(change is null) ||
				string.IsNullOrWhiteSpace(change.Name)) ||
			(changes
				.Select(change => change.Name)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Count() != changes.Count))
		{
			throw new ArgumentException(
				"The AGY current-user environment transaction is invalid.",
				nameof(changes));
		}

		string?[] previousValues = changes
			.Select(change => getEnvironmentVariable(
				change.Name,
				EnvironmentVariableTarget.User))
			.ToArray();
		int attemptedChangeCount = 0;

		try
		{
			for (int index = 0; index < changes.Count; index++)
			{
				UserEnvironmentChange change = changes[index];
				attemptedChangeCount = index + 1;
				setEnvironmentVariable(
					change.Name,
					change.Value,
					EnvironmentVariableTarget.User);
			}

			for (int index = 0; index < changes.Count; index++)
			{
				UserEnvironmentChange change = changes[index];
				string? observed = getEnvironmentVariable(
					change.Name,
					EnvironmentVariableTarget.User);

				if (!EnvironmentValuesEqual(observed, change.Value))
				{
					throw new InvalidOperationException(
						"The current-user AGY source setting could not be verified.");
				}
			}

			commit?.Invoke();
		}
		catch (Exception failure)
		{
			List<Exception> rollbackFailures = new();

			for (int index = attemptedChangeCount - 1; index >= 0; index--)
			{
				UserEnvironmentChange change = changes[index];

				try
				{
					setEnvironmentVariable(
						change.Name,
						previousValues[index],
						EnvironmentVariableTarget.User);
					string? restored = getEnvironmentVariable(
						change.Name,
						EnvironmentVariableTarget.User);

					if (!EnvironmentValuesEqual(
						restored,
						previousValues[index]))
					{
						throw new InvalidOperationException(
							"An AGY source setting rollback could not be verified.");
					}
				}
				catch (Exception rollbackFailure)
				{
					rollbackFailures.Add(rollbackFailure);
				}
			}

			if (rollbackFailures.Count > 0)
			{
				throw new InvalidOperationException(
					"The AGY source setting failed and rollback was incomplete.",
					new AggregateException(
						new[] { failure }.Concat(rollbackFailures)));
			}

			throw;
		}
	}

	private static bool EnvironmentValuesEqual(
		string? left,
		string? right)
	{
		return (left is null) && (right is null) ||
			string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
	}

	internal static IReadOnlyDictionary<string, string>
		BuildCaptureEnvironment(string userProfile)
	{
		Dictionary<string, string> environment = new(
			StringComparer.OrdinalIgnoreCase);

		foreach (string name in CopiedEnvironmentVariableNames)
		{
			string? value = Environment.GetEnvironmentVariable(name);

			if (value is not null)
			{
				environment[name] = value;
			}
		}

		environment["USERPROFILE"] = userProfile;
		environment[AutoUpdateEnvironmentVariable] = "true";
		return environment;
	}

	internal static async Task<IReadOnlyList<string>>
		DiscoverExecutableCandidatesAsync(
			string localApplicationData,
			string? pathEnvironmentVariable,
			Func<string, bool> fileExists,
			CancellationToken cancellationToken)
	{
		return await DiscoverExecutableCandidatesAsync(
				localApplicationData,
				pathEnvironmentVariable,
				fileExists,
				GetExecutableSearchDriveType,
				ExecutableDiscoveryTimeout,
				cancellationToken)
			.ConfigureAwait(false);
	}

	internal static async Task<IReadOnlyList<string>>
		DiscoverExecutableCandidatesAsync(
			string localApplicationData,
			string? pathEnvironmentVariable,
		Func<string, bool> fileExists,
		Func<string, AntigravityExecutableSearchDriveType> getDriveType,
		CancellationToken cancellationToken)
	{
		return await DiscoverExecutableCandidatesAsync(
				localApplicationData,
				pathEnvironmentVariable,
				fileExists,
				getDriveType,
				ExecutableDiscoveryTimeout,
				cancellationToken)
			.ConfigureAwait(false);
	}

	internal static async Task<IReadOnlyList<string>>
		DiscoverExecutableCandidatesAsync(
			string localApplicationData,
			string? pathEnvironmentVariable,
			Func<string, bool> fileExists,
			Func<string, AntigravityExecutableSearchDriveType> getDriveType,
			TimeSpan timeout,
			CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(fileExists);
		ArgumentNullException.ThrowIfNull(getDriveType);

		if (timeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		cancellationToken.ThrowIfCancellationRequested();
		long waitStartedTimestamp = Stopwatch.GetTimestamp();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ExecutableDiscoveryOperation operation;
			bool isOwner;

			lock (ExecutableDiscoverySyncRoot)
			{
				if ((_activeExecutableDiscovery is null) ||
					_activeExecutableDiscovery.Task.IsCompleted)
				{
					CancellationTokenSource operationCancellation = new();
					Task<string[]> discoveryTask = Task.Run(
						() => DiscoverExecutableCandidates(
								localApplicationData,
								pathEnvironmentVariable,
								fileExists,
								getDriveType,
								operationCancellation.Token)
							.ToArray(),
						operationCancellation.Token);
					operation = new ExecutableDiscoveryOperation(
						operationCancellation,
						discoveryTask);
					_activeExecutableDiscovery = operation;
					isOwner = true;
					_ = ObserveDiscoveryCompletionAsync(operation);
				}
				else
				{
					operation = _activeExecutableDiscovery;
					isOwner = false;
				}
			}

			TimeSpan remaining = timeout -
				Stopwatch.GetElapsedTime(waitStartedTimestamp);

			if (remaining <= TimeSpan.Zero)
			{
				if (isOwner)
				{
					operation.TryCancel();
				}

				throw new TimeoutException(
					"AGY executable discovery did not finish before its time limit.");
			}

			try
			{
				string[] candidates = await operation.Task
					.WaitAsync(remaining, cancellationToken)
					.ConfigureAwait(false);

				if (isOwner)
				{
					return candidates;
				}
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				if (isOwner)
				{
					operation.TryCancel();
				}

				throw;
			}
			catch (TimeoutException)
			{
				if (isOwner)
				{
					operation.TryCancel();
				}

				throw new TimeoutException(
					"AGY executable discovery did not finish before its time limit.");
			}
			catch when (!isOwner)
			{
				// A previous caller's cancelled or failed operation is only a
				// single-flight barrier. Retry this caller's own discovery while
				// preserving its original overall timeout.
			}
		}
	}

	internal static bool IsLocalExecutableSearchDirectory(string path)
	{
		return IsLocalExecutableSearchDirectory(
			path,
			GetExecutableSearchDriveType);
	}

	internal static bool IsLocalExecutableSearchDirectory(
		string path,
		Func<string, AntigravityExecutableSearchDriveType> getDriveType)
	{
		ArgumentNullException.ThrowIfNull(getDriveType);

		try
		{
			if (string.IsNullOrWhiteSpace(path) ||
				!Path.IsPathFullyQualified(path))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(path);
			string? root = Path.GetPathRoot(fullPath);

			if (!string.IsNullOrWhiteSpace(root) &&
				!root.StartsWith(
					new string(Path.DirectorySeparatorChar, 2),
					StringComparison.Ordinal) &&
				(root.Length >= 3) &&
				char.IsAsciiLetter(root[0]) &&
				(root[1] == Path.VolumeSeparatorChar) &&
				((root[2] == Path.DirectorySeparatorChar) ||
					(root[2] == Path.AltDirectorySeparatorChar)))
			{
				AntigravityExecutableSearchDriveType driveType =
					getDriveType(root);
				return
					(driveType ==
						AntigravityExecutableSearchDriveType.Fixed) ||
					(driveType ==
						AntigravityExecutableSearchDriveType.RamDisk);
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	private static IEnumerable<string> DiscoverExecutableCandidates(
		string localApplicationData,
		string? pathEnvironmentVariable,
		Func<string, bool> fileExists,
		Func<string, AntigravityExecutableSearchDriveType> getDriveType,
		CancellationToken cancellationToken)
	{
		HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
		cancellationToken.ThrowIfCancellationRequested();

		TryAddExecutableCandidate(
			candidates,
			Path.Combine(localApplicationData, "agy", "bin", "agy.exe"),
			fileExists,
			getDriveType);
		cancellationToken.ThrowIfCancellationRequested();
		TryAddExecutableCandidate(
			candidates,
			Path.Combine(
				localApplicationData,
				"Programs",
				"Antigravity",
				"resources",
				"app",
				"bin",
				"agy.exe"),
			fileExists,
			getDriveType);
		cancellationToken.ThrowIfCancellationRequested();

		int localPathEntryCount = 0;

		foreach (string pathEntry in (pathEnvironmentVariable ?? string.Empty)
				.Split(
					Path.PathSeparator,
					StringSplitOptions.RemoveEmptyEntries |
					StringSplitOptions.TrimEntries))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string normalizedEntry = pathEntry.Trim('"');

			if (!IsLocalExecutableSearchDirectory(
					normalizedEntry,
					getDriveType))
			{
				continue;
			}

			if (localPathEntryCount >= MaximumPathEntryCount)
			{
				break;
			}

			localPathEntryCount++;
			TryAddExecutableCandidate(
				candidates,
				Path.Combine(normalizedEntry, "agy.exe"),
				fileExists,
				getDriveType);
			cancellationToken.ThrowIfCancellationRequested();
		}

		return candidates.OrderBy(
			candidate => candidate,
			StringComparer.OrdinalIgnoreCase);
	}

	private async Task<SupportedBuild?> FindSupportedBuildAsync(
		IProgress<AntigravityMachineSetupProgress>? progress,
		CancellationToken cancellationToken)
	{
		progress?.Report(new AntigravityMachineSetupProgress(
			AntigravityMachineSetupStage.DiscoveringExecutable));
		List<OfficialPrintSupportedBuild> officialMatches = new();
		string? configuredOfficialExecutable = _getEnvironmentVariable(
			AntigravityMachineSetupEnvironmentVariables.OfficialExecutable,
			EnvironmentVariableTarget.User);

		if (string.IsNullOrWhiteSpace(configuredOfficialExecutable))
		{
			configuredOfficialExecutable = _getEnvironmentVariable(
				AntigravityMachineSetupEnvironmentVariables.OfficialExecutable,
				EnvironmentVariableTarget.Process);
		}

		bool requiresOfficialPrint =
			!string.IsNullOrWhiteSpace(configuredOfficialExecutable);
		IReadOnlyList<string> discoveredCandidates =
			await _discoverExecutableCandidatesAsync(cancellationToken)
				.ConfigureAwait(false);
		HashSet<string> executableCandidates = new(
			discoveredCandidates,
			StringComparer.OrdinalIgnoreCase);

		if (requiresOfficialPrint)
		{
			TryAddExecutableCandidate(
				executableCandidates,
				configuredOfficialExecutable!,
				File.Exists,
				GetExecutableSearchDriveType);
		}

		foreach (string executablePath in executableCandidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			AntigravityOfficialPrintCapabilityValidationResult result;

			try
			{
				result = await _validateOfficialPrintCapabilityAsync(
					executablePath,
					cancellationToken);
			}
			catch (AntigravityOfficialPrintExecutionGateException exception)
			{
				throw new SetupFailureException(
					exception.FailureKind ==
					AntigravityOfficialPrintExecutionGateFailureKind
						.ContentionTimedOut
						? AntigravityMachineSetupFailureKind.ExecutionBusy
						: AntigravityMachineSetupFailureKind
							.UnexpectedFailure);
			}

			using (result)
			{
				if (result is null)
				{
					throw new InvalidOperationException(
						"The official AGY capability validator returned no result.");
				}

				if (result.IsSupported &&
					!string.IsNullOrWhiteSpace(result.CliVersion) &&
					(result.ExecutableLease is not null) &&
					!result.ExecutableLease.IsDisposed)
				{
					officialMatches.Add(new OfficialPrintSupportedBuild(
						result.CliVersion,
						result.ExecutableLease.AbsolutePath));
				}
			}
		}

		if (officialMatches.Count > 1)
		{
			throw new SetupFailureException(
				AntigravityMachineSetupFailureKind.AmbiguousExecutable);
		}

		if (officialMatches.Count == 1)
		{
			return officialMatches[0];
		}

		// Once an official-print marker has been approved, an invalid or
		// missing official executable must not silently reactivate a legacy
		// private profile. Discovery may still repair the marker by finding
		// another valid official-print installation.
		if (requiresOfficialPrint)
		{
			return null;
		}

		ReviewedConPtyEligibilityReceipt? existingLegacyEligibility =
			await TryLoadExistingReviewedConPtyEligibilityAsync(
				cancellationToken).ConfigureAwait(false);

		if (existingLegacyEligibility is null)
		{
			return null;
		}

		List<ReviewedConPtySupportedBuild> legacyMatches = new();

		foreach (string executablePath in executableCandidates)
		{
			cancellationToken.ThrowIfCancellationRequested();

			using AntigravityCliCapabilityValidationResult result =
				await _validateReviewedConPtyCapabilityAsync(
					executablePath,
					existingLegacyEligibility.Contract,
					cancellationToken);

			if (result.IsSupported &&
				(result.ObservedFingerprint is not null))
			{
				legacyMatches.Add(new ReviewedConPtySupportedBuild(
					existingLegacyEligibility,
					result.ObservedFingerprint));
			}
		}

		if (legacyMatches.Count > 1)
		{
			throw new SetupFailureException(
				AntigravityMachineSetupFailureKind.AmbiguousExecutable);
		}

		return legacyMatches.SingleOrDefault();
	}

	private async Task<ReviewedConPtyEligibilityReceipt?>
		TryLoadExistingReviewedConPtyEligibilityAsync(
			CancellationToken cancellationToken)
	{
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;

		try
		{
			string? configuredProfile = _getEnvironmentVariable(
				ProfileEnvironmentVariable,
				EnvironmentVariableTarget.User);

			if (string.IsNullOrWhiteSpace(configuredProfile) ||
				!Path.IsPathFullyQualified(configuredProfile))
			{
				return null;
			}

			string profilePath = Path.GetFullPath(configuredProfile);
			profileBytes = await AntigravityLiveR0PrivateProfileFile
				.ReadBoundedAsync(
					profilePath,
					MaximumExistingProfileBytes,
					cancellationToken).ConfigureAwait(false);
			AntigravityLiveR1SectionProfileFile profileFile =
				AntigravityLiveR1SectionProfileJson.Deserialize(profileBytes);
			AntigravityReviewedPackageContract? matchingContract =
				AntigravityReviewedPackageManifestCatalog.FindMatchingContract(
					profileFile);

			if (matchingContract is null)
			{
				return null;
			}

			(AntigravityLiveR1SectionProfile profile, byte[] loadedHmacKey) =
				await profileFile.ToProfileAsync(
					profilePath,
					cancellationToken).ConfigureAwait(false);
			hmacKey = loadedHmacKey;
			string settingsBaselineFingerprint =
				await _captureReviewedConPtySettingsBaselineAsync(
					profile.CaptureProfile,
					cancellationToken).ConfigureAwait(false);
			string currentCaptureContractFingerprint =
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					profile.CaptureProfile,
					profile.UsageLayout.Spec.IsAlternateScreen,
					settingsBaselineFingerprint);

			if (!string.Equals(
					profile.UsageLayout.CaptureContractFingerprint,
					currentCaptureContractFingerprint,
					StringComparison.Ordinal) ||
				!AntigravityReviewedPackageLocalBinding.Matches(
					matchingContract.ContractFingerprint,
					currentCaptureContractFingerprint,
					hmacKey,
					profile.UsageLayout.ApprovedDraftFingerprint))
			{
				return null;
			}

			string? confirmedProfile = _getEnvironmentVariable(
				ProfileEnvironmentVariable,
				EnvironmentVariableTarget.User);

			if (string.IsNullOrWhiteSpace(confirmedProfile) ||
				!Path.IsPathFullyQualified(confirmedProfile) ||
				!string.Equals(
					Path.GetFullPath(confirmedProfile),
					profilePath,
					StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return new ReviewedConPtyEligibilityReceipt(
				matchingContract,
				profilePath,
				Convert.ToHexString(SHA256.HashData(profileBytes)),
				currentCaptureContractFingerprint,
				settingsBaselineFingerprint,
				profile.UsageLayout.ApprovedDraftFingerprint);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}
		}
	}

	private static bool IsSameReviewedConPtyEligibility(
		ReviewedConPtyEligibilityReceipt expected,
		ReviewedConPtyEligibilityReceipt current)
	{
		return
			string.Equals(
				expected.ProfilePath,
				current.ProfilePath,
				StringComparison.OrdinalIgnoreCase) &&
			string.Equals(
				expected.Contract.ContractFingerprint,
				current.Contract.ContractFingerprint,
				StringComparison.Ordinal) &&
			string.Equals(
				expected.ProfileBytesSha256,
				current.ProfileBytesSha256,
				StringComparison.Ordinal) &&
			string.Equals(
				expected.CaptureContractFingerprint,
				current.CaptureContractFingerprint,
				StringComparison.Ordinal) &&
			string.Equals(
				expected.SettingsBaselineFingerprint,
				current.SettingsBaselineFingerprint,
				StringComparison.Ordinal) &&
			string.Equals(
				expected.LocalBinding,
				current.LocalBinding,
				StringComparison.Ordinal);
	}

	private static AntigravityMachineSetupPreparationResult Failure(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return new AntigravityMachineSetupPreparationResult(failureKind);
	}

	private static AntigravityMachineSetupPreparationResult PreviewFailure(
		AntigravityMachineSetupSourceKind sourceKind,
		AntigravityProductionUsageResult preview,
		Func<
			CancellationToken,
			Task<AntigravityMachineSetupPreparationResult>>?
			revalidateSafetyAsync = null)
	{
		AntigravityMachineSetupFailureKind failureKind =
			preview.FailureKind switch
			{
				AntigravityProductionUsageFailureKind.ExecutionBusy =>
					AntigravityMachineSetupFailureKind.ExecutionBusy,
				AntigravityProductionUsageFailureKind.ProfileRejected =>
					AntigravityMachineSetupFailureKind.SettingsRejected,
				AntigravityProductionUsageFailureKind.UsageShapeRejected =>
					AntigravityMachineSetupFailureKind.UsageRejected,
				AntigravityProductionUsageFailureKind.SafetyLatched when
					(sourceKind ==
						AntigravityMachineSetupSourceKind.OfficialPrint) &&
					!preview.AutomaticRevalidationPending =>
					AntigravityMachineSetupFailureKind
						.SafetyRevalidationRequired,
				_ => AntigravityMachineSetupFailureKind.UnexpectedFailure
			};

		return new AntigravityMachineSetupPreparationResult(
			failureKind,
			sourceKind,
			preview,
			revalidateSafetyAsync);
	}

	internal static bool IsSafePromptCalibration(
		AntigravityLiveR0Report report)
	{
		return
			report.IsCaptureSuccessful &&
			(report.Mode == AntigravityLiveR0Mode.CalibratePrompt) &&
			!report.ExistingProcessDetected &&
			(report.ExistingProcessGate ==
				AntigravityLiveR0GateStatus.Passed) &&
			(report.CapabilityGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.PromptGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.UsageCaptureGate ==
				AntigravityLiveR0GateStatus.NotApplicable) &&
			(report.SettingsGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.CleanupGate == AntigravityLiveR0GateStatus.Passed) &&
			(report.IdentityGate == AntigravityLiveR0GateStatus.NotVerified) &&
			(report.ModelInvocationGate ==
				AntigravityLiveR0GateStatus.NotVerified) &&
			(report.InputWriteAttemptCount == 0) &&
			(report.InputWriteCount == 0) &&
			(report.FailureReasons.Count == 0) &&
			(report.PromptCapture is not null) &&
			AntigravityUsageR1SchemaParser.IsFingerprint(
				report.PromptExactLocalFingerprint);
	}

	private async Task<AntigravityMachineSetupPreparationResult>
		PrepareOfficialPrintBuildAsync(
			OfficialPrintSupportedBuild supportedBuild,
			IProgress<AntigravityMachineSetupProgress>? progress,
			CancellationToken cancellationToken,
			bool revalidateSafety = false)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			progress?.Report(new AntigravityMachineSetupProgress(
				AntigravityMachineSetupStage.ValidatingUsage));
			AntigravityProductionUsageResult preview = revalidateSafety
				? await _officialPrintUsageClient.RevalidateAsync(
					supportedBuild.ExecutablePath,
					static () => { },
					cancellationToken)
				: await _officialPrintUsageClient.CaptureAsync(
					supportedBuild.ExecutablePath,
					cancellationToken);

			if (preview is null)
			{
				return Failure(
					AntigravityMachineSetupFailureKind.UnexpectedFailure);
			}

			if (!preview.IsSuccessful)
			{
				Func<
					CancellationToken,
					Task<AntigravityMachineSetupPreparationResult>>?
					revalidateSafetyAsync =
					preview.FailureKind ==
						AntigravityProductionUsageFailureKind.SafetyLatched &&
					!preview.AutomaticRevalidationPending
						? token => PrepareOfficialPrintBuildAsync(
							supportedBuild,
							progress,
							token,
							revalidateSafety: true)
						: null;
				return PreviewFailure(
					AntigravityMachineSetupSourceKind.OfficialPrint,
					preview,
					revalidateSafetyAsync);
			}

			if ((preview.FailureKind !=
					AntigravityProductionUsageFailureKind.None) ||
				string.IsNullOrWhiteSpace(preview.AccountIdentity) ||
				(preview.Windows.Count != 4))
			{
				return Failure(
					AntigravityMachineSetupFailureKind.UsageRejected);
			}

			AntigravityMachineSetupCandidate candidate = new(
				supportedBuild.CliVersion,
				preview.AccountIdentity,
				preview.Windows,
				approvalCancellationToken => ApproveOfficialPrintAsync(
					supportedBuild.ExecutablePath,
					supportedBuild.CliVersion,
					approvalCancellationToken),
				() => ValueTask.CompletedTask,
				AntigravityMachineSetupSourceKind.OfficialPrint);
			progress?.Report(new AntigravityMachineSetupProgress(
				AntigravityMachineSetupStage.ReadyForApproval));
			return new AntigravityMachineSetupPreparationResult(candidate);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return Failure(
				AntigravityMachineSetupFailureKind.UnexpectedFailure);
		}
	}

	private async Task<AntigravityMachineSetupPreparationResult>
		PrepareReviewedConPtyBuildAsync(
			ReviewedConPtySupportedBuild supportedBuild,
			IProgress<AntigravityMachineSetupProgress>? progress,
			CancellationToken cancellationToken)
	{
		AntigravityMachineSetupPrivateTransaction? transaction = null;
		byte[]? hmacKey = null;
		byte[]? profileBytes = null;
		bool isHandedOff = false;

		try
		{
			string localApplicationData = _getFolderPath(
				Environment.SpecialFolder.LocalApplicationData);
			string userProfile = _getFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(localApplicationData) ||
				string.IsNullOrWhiteSpace(userProfile) ||
				!Path.IsPathFullyQualified(localApplicationData) ||
				!Path.IsPathFullyQualified(userProfile) ||
				!Directory.Exists(userProfile))
			{
				throw new SetupFailureException(
					AntigravityMachineSetupFailureKind.SettingsRejected);
			}

			string privateRoot = Path.Combine(
				localApplicationData,
				"AiUsageDashboard",
				"antigravity",
				"private");
			string settingsPath = Path.Combine(
				userProfile,
				".gemini",
				"antigravity-cli",
				"settings.json");

			if (!File.Exists(settingsPath))
			{
				throw new SetupFailureException(
					AntigravityMachineSetupFailureKind.SettingsRejected);
			}

			progress?.Report(new AntigravityMachineSetupProgress(
				AntigravityMachineSetupStage.PreparingPrivateStorage));
			try
			{
				transaction =
					await AntigravityMachineSetupPrivateTransaction.CreateAsync(
						privateRoot,
						supportedBuild.Contract.ContractId,
						cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				throw new SetupFailureException(
					AntigravityMachineSetupFailureKind
						.PrivateStorageRejected);
			}
			AntigravityReviewedPackageUsageLayout reviewedLayout =
				supportedBuild.Contract.UsageLayout;
			AntigravityUsageR1SectionSpec spec = reviewedLayout.Spec;
			IReadOnlyDictionary<string, string> environment =
				BuildCaptureEnvironment(userProfile);
			AntigravityLiveR0ProfileFile calibrationProfileFile = new(
				$"setup-{supportedBuild.Contract.ContractId}",
				supportedBuild.Fingerprint,
				userProfile,
				environment,
				checked((short)spec.Columns),
				checked((short)spec.Rows),
				ExpectedPromptStructuralFingerprint: string.Empty,
				transaction.KeyPath,
				ExpectedExactPromptFingerprint: string.Empty,
				ExpectedUsageStructuralFingerprint: null,
				Array.AsReadOnly(new[] { settingsPath }),
				MaximumSavedOutputBytes,
				PromptTimeout,
				UsageTimeout,
				StableScreenDuration,
				PollInterval,
				CleanupTimeout,
				ExpectedExactUsageFingerprint: null);
			(AntigravityLiveR0Profile calibrationProfile,
				byte[] loadedHmacKey) =
				await calibrationProfileFile.ToProfileAsync(
					transaction.ProfilePath,
					cancellationToken);
			hmacKey = loadedHmacKey;
			progress?.Report(new AntigravityMachineSetupProgress(
				AntigravityMachineSetupStage.CalibratingPrompt));
			AntigravityLiveR0Report promptReport =
				await _runReviewedConPtyPromptCalibrationAsync(
				calibrationProfile,
				cancellationToken);

			if (!IsSafePromptCalibration(promptReport))
			{
				AntigravityMachineSetupFailureKind failureKind =
					promptReport.ExistingProcessDetected ||
					promptReport.FailureReasons.Contains(
						AntigravityLiveR0FailureReason
							.ExistingProcessDetected)
						? AntigravityMachineSetupFailureKind
							.ExistingProcessDetected
						: AntigravityMachineSetupFailureKind.PromptRejected;
				throw new SetupFailureException(failureKind);
			}

			string observedStructuralFingerprint =
				promptReport.PromptCapture!.StructuralFingerprint;
			string observedExactFingerprint =
				promptReport.PromptExactLocalFingerprint!;

			if (!string.Equals(
					observedStructuralFingerprint,
					supportedBuild.Contract
						.ExpectedPromptStructuralFingerprint,
					StringComparison.Ordinal))
			{
				throw new SetupFailureException(
					AntigravityMachineSetupFailureKind.PromptRejected);
			}

			AntigravityLiveR0Profile pinnedProfile = calibrationProfile with
			{
				ExpectedPromptStructuralFingerprint =
					observedStructuralFingerprint,
				ExpectedExactPromptFingerprint = observedExactFingerprint
			};
			string settingsBaselineFingerprint;

			try
			{
				settingsBaselineFingerprint =
					await _captureReviewedConPtySettingsBaselineAsync(
						pinnedProfile,
						cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				throw new SetupFailureException(
					AntigravityMachineSetupFailureKind.SettingsRejected);
			}

			progress?.Report(new AntigravityMachineSetupProgress(
				AntigravityMachineSetupStage.MaterializingProfile));
			string captureContractFingerprint =
				AntigravityLiveR1PrivateCalibrationCaptureContract.Compute(
					pinnedProfile,
					spec.IsAlternateScreen,
					settingsBaselineFingerprint);
			string localReviewBinding =
				AntigravityReviewedPackageLocalBinding.Compute(
				supportedBuild.Contract.ContractFingerprint,
				captureContractFingerprint,
				hmacKey);

			// The v3 field name reflects the dev promotion path. For the
			// packaged path it stores a private HMAC binding to the separately
			// reviewed embedded contract; it is never exported or trusted alone.
			AntigravityUsageR1SectionLayoutFile localLayout = new(
				AntigravityUsageR1SectionSchemaParser.LayoutFormatVersion,
				AntigravityUsageR1ReviewState.Reviewed,
				spec,
				reviewedLayout.SchemaFingerprint,
				reviewedLayout.ExpectedPageFingerprints,
				captureContractFingerprint,
				localReviewBinding);
			_ = localLayout.ToLayout();
			AntigravityLiveR1SectionProfileFile finalProfile = new(
				AntigravityLiveR1SectionProfileFile.CurrentSchemaVersion,
				pinnedProfile.Id,
				supportedBuild.Fingerprint,
				pinnedProfile.WorkingDirectory,
				pinnedProfile.Environment,
				pinnedProfile.Columns,
				pinnedProfile.Rows,
				new AntigravityLiveR1PromptGuard(
					observedStructuralFingerprint,
					observedExactFingerprint,
					transaction.KeyPath),
				localLayout,
				pinnedProfile.NonCredentialSettingsFiles,
				pinnedProfile.MaximumSavedOutputBytes,
				pinnedProfile.PromptTimeout,
				pinnedProfile.UsageTimeout,
				pinnedProfile.StableScreenDuration,
				pinnedProfile.PollInterval,
				pinnedProfile.CleanupTimeout);
			profileBytes = AntigravityLiveR1SectionProfileJson.Serialize(
				finalProfile);
			await transaction.WriteProfileAsync(
				profileBytes,
				cancellationToken);
			CryptographicOperations.ZeroMemory(profileBytes);
			profileBytes = null;
			progress?.Report(new AntigravityMachineSetupProgress(
				AntigravityMachineSetupStage.ValidatingUsage));
			AntigravityProductionUsageResult preview =
				await _reviewedConPtyUsageClient.CaptureAsync(
					transaction.ProfilePath,
					cancellationToken);

			if (!preview.IsSuccessful)
			{
				await transaction.DisposeAsync();

				if (!transaction.CleanupSucceeded)
				{
					return Failure(
						AntigravityMachineSetupFailureKind
							.PrivateStorageRejected);
				}

				return PreviewFailure(
					AntigravityMachineSetupSourceKind.ReviewedConPty,
					preview);
			}

			if ((preview.FailureKind !=
					AntigravityProductionUsageFailureKind.None) ||
				string.IsNullOrWhiteSpace(preview.AccountIdentity) ||
				(preview.Windows.Count != 4))
			{
				throw new SetupFailureException(
					AntigravityMachineSetupFailureKind.UsageRejected);
			}

			AntigravityMachineSetupPrivateTransaction ownedTransaction =
				transaction;
			AntigravityMachineSetupCandidate candidate = new(
				supportedBuild.Fingerprint.CliVersion,
				preview.AccountIdentity,
				preview.Windows,
				approvalCancellationToken => ApproveReviewedConPtyAsync(
					ownedTransaction,
					supportedBuild.Eligibility,
					approvalCancellationToken),
				() => ReleaseAsync(ownedTransaction),
				AntigravityMachineSetupSourceKind.ReviewedConPty);
			isHandedOff = true;
			progress?.Report(new AntigravityMachineSetupProgress(
				AntigravityMachineSetupStage.ReadyForApproval));
			return new AntigravityMachineSetupPreparationResult(candidate);
		}
		catch (OperationCanceledException)
		{
			if (transaction is not null)
			{
				await transaction.DisposeAsync();

				if (!transaction.CleanupSucceeded)
				{
					return Failure(
						AntigravityMachineSetupFailureKind
							.PrivateStorageRejected);
				}
			}

			throw;
		}
		catch (SetupFailureException exception)
		{
			return await DisposeAndFailAsync(
				transaction,
				exception.FailureKind);
		}
		catch
		{
			return await DisposeAndFailAsync(
				transaction,
				AntigravityMachineSetupFailureKind.UnexpectedFailure);
		}
		finally
		{
			if (profileBytes is not null)
			{
				CryptographicOperations.ZeroMemory(profileBytes);
			}

			if (hmacKey is not null)
			{
				CryptographicOperations.ZeroMemory(hmacKey);
			}

			if (!isHandedOff &&
				(transaction is not null) &&
				!transaction.CleanupSucceeded)
			{
				await transaction.DisposeAsync();
			}
		}
	}

	private static async Task<AntigravityMachineSetupPreparationResult>
		DisposeAndFailAsync(
			AntigravityMachineSetupPrivateTransaction? transaction,
			AntigravityMachineSetupFailureKind failureKind)
	{
		if (transaction is null)
		{
			return Failure(failureKind);
		}

		await transaction.DisposeAsync();
		return Failure(
			transaction.CleanupSucceeded
				? failureKind
				: AntigravityMachineSetupFailureKind.PrivateStorageRejected);
	}

	private static async ValueTask ReleaseAsync(
		AntigravityMachineSetupPrivateTransaction transaction)
	{
		await transaction.DisposeAsync();

		if (!transaction.CleanupSucceeded)
		{
			throw new IOException(
				"The AGY setup transaction could not verify its final state.");
		}
	}

	private static void TryAddExecutableCandidate(
		ISet<string> candidates,
		string candidate,
		Func<string, bool> fileExists,
		Func<string, AntigravityExecutableSearchDriveType> getDriveType)
	{
		try
		{
			string? searchDirectory = Path.GetDirectoryName(candidate);

			if ((searchDirectory is not null) &&
				IsLocalExecutableSearchDirectory(
					searchDirectory,
					getDriveType) &&
				fileExists(candidate))
			{
				candidates.Add(Path.GetFullPath(candidate));
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			// Discovery is best effort. Exact capability validation is mandatory.
		}
	}

	private static AntigravityExecutableSearchDriveType
		GetExecutableSearchDriveType(string rootPath)
	{
		if (!OperatingSystem.IsWindows())
		{
			return AntigravityExecutableSearchDriveType.Unknown;
		}

		return (AntigravityExecutableSearchDriveType)GetDriveTypeNative(
			rootPath);
	}

	private static async Task ObserveDiscoveryCompletionAsync(
		ExecutableDiscoveryOperation operation)
	{
		try
		{
			await operation.Task.ConfigureAwait(false);
		}
		catch
		{
			// The initiating caller may already have cancelled or timed out.
			// Observing the terminal state prevents an abandoned probe from
			// leaking an exception.
		}
		finally
		{
			lock (ExecutableDiscoverySyncRoot)
			{
				if (ReferenceEquals(
						_activeExecutableDiscovery,
						operation))
				{
					_activeExecutableDiscovery = null;
				}
			}

			operation.CancellationSource.Dispose();
		}
	}

	[DllImport(
		"kernel32.dll",
		EntryPoint = "GetDriveTypeW",
		CharSet = CharSet.Unicode)]
	private static extern uint GetDriveTypeNative(string rootPathName);
}
