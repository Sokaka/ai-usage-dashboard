using System.Diagnostics;
using System.Runtime.InteropServices;

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
	private sealed record SupportedBuild(
		string CliVersion,
		string ExecutablePath);

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
			AntigravityMachineSetupFailureKind failureKind,
			Exception? innerException = null)
			: base("The AGY machine setup failed closed.", innerException)
		{
			if ((failureKind == AntigravityMachineSetupFailureKind.None) ||
				!Enum.IsDefined(failureKind))
			{
				throw new ArgumentOutOfRangeException(nameof(failureKind));
			}

			FailureKind = failureKind;
		}
	}

	private const string SourceMutationSemaphoreName =
		@"Local\AiUsageDashboard.AntigravityMachineSetup.SourceMutation.v1";
	private const int MaximumPathEntryCount = 256;
	private static readonly TimeSpan ExecutableDiscoveryTimeout =
		TimeSpan.FromSeconds(10);
	private static readonly TimeSpan SourceMutationWaitTimeout =
		TimeSpan.FromSeconds(10);
	private static readonly object ExecutableDiscoverySyncRoot = new();
	private static ExecutableDiscoveryOperation? _activeExecutableDiscovery;
	private readonly IAntigravityApprovedSourceStore _approvedSourceStore;
	private readonly AntigravityApprovedSourceResolver _approvedSourceResolver;
	private readonly Func<
		CancellationToken,
		Task<IReadOnlyList<string>>> _discoverExecutableCandidatesAsync;
	private readonly IAntigravityOfficialPrintUsageClient
		_officialPrintUsageClient;
	private readonly Func<
		string,
		CancellationToken,
		Task<AntigravityOfficialPrintCapabilityValidationResult>>
		_validateOfficialPrintCapabilityAsync;

	public AntigravityMachineSetupService()
		: this(CreateDefaultOfficialPrintUsageClient())
	{
	}

	public AntigravityMachineSetupService(
		IAntigravityOfficialPrintUsageClient officialPrintUsageClient)
		: this(
			CreateOfficialPrintCapabilityValidator(officialPrintUsageClient),
			officialPrintUsageClient,
			JsonAntigravityApprovedSourceStore.CreateDefault(),
			Environment.GetEnvironmentVariable)
	{
	}

	internal AntigravityMachineSetupService(
		Func<
			string,
			CancellationToken,
			Task<AntigravityOfficialPrintCapabilityValidationResult>>
			validateOfficialPrintCapabilityAsync,
		IAntigravityOfficialPrintUsageClient officialPrintUsageClient,
		IAntigravityApprovedSourceStore approvedSourceStore,
		Func<string, EnvironmentVariableTarget, string?>
			getEnvironmentVariable,
		Func<CancellationToken, Task<IReadOnlyList<string>>>?
			discoverExecutableCandidatesAsync = null)
	{
		_validateOfficialPrintCapabilityAsync =
			validateOfficialPrintCapabilityAsync ??
			throw new ArgumentNullException(
				nameof(validateOfficialPrintCapabilityAsync));
		_officialPrintUsageClient = officialPrintUsageClient ??
			throw new ArgumentNullException(nameof(officialPrintUsageClient));
		_approvedSourceStore = approvedSourceStore ??
			throw new ArgumentNullException(nameof(approvedSourceStore));
		ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
		_approvedSourceResolver = new AntigravityApprovedSourceResolver(
			_approvedSourceStore,
			name => getEnvironmentVariable(
				name,
				EnvironmentVariableTarget.Process),
			getEnvironmentVariable);
		_discoverExecutableCandidatesAsync =
			discoverExecutableCandidatesAsync ??
			DiscoverCurrentExecutableCandidatesAsync;
	}

	public async Task<AntigravityMachineSetupPreparationResult> PrepareAsync(
		AntigravityMachineSetupConsent consent,
		IProgress<AntigravityMachineSetupProgress>? progress,
		CancellationToken cancellationToken)
	{
		if ((consent is null) || !consent.IsOfficialUsageReadApproved)
		{
			return Failure(
				AntigravityMachineSetupFailureKind.ConsentRequired);
		}

		cancellationToken.ThrowIfCancellationRequested();
		SupportedBuild? supportedBuild;
		try
		{
			supportedBuild = await FindSupportedBuildAsync(
				progress,
				cancellationToken).ConfigureAwait(false);
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

		return supportedBuild is null
			? Failure(AntigravityMachineSetupFailureKind.UnsupportedBuild)
			: await PrepareOfficialPrintBuildAsync(
				supportedBuild,
				progress,
				cancellationToken).ConfigureAwait(false);
	}

	internal static void ApplyApprovedSourceChange(
		AntigravityApprovedSource source,
		IAntigravityApprovedSourceStore store,
		CancellationToken cancellationToken,
		Action? commit = null)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(store);
		cancellationToken.ThrowIfCancellationRequested();
		AntigravityApprovedSource? previousSource = store.Load();

		try
		{
			store.Save(source);
			EnsureApprovedSourcesEqual(store.Load(), source);
			commit?.Invoke();
		}
		catch (Exception failure)
		{
			try
			{
				if (previousSource is null)
				{
					store.Clear();
				}
				else
				{
					store.Save(previousSource);
				}

				EnsureApprovedSourcesEqual(store.Load(), previousSource);
			}
			catch (Exception rollbackFailure)
			{
				throw new InvalidOperationException(
					"The AGY source setting failed and rollback was incomplete.",
					new AggregateException(failure, rollbackFailure));
			}

			throw;
		}
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
				// A previous caller's failed probe is only a single-flight barrier.
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
				return driveType is
					AntigravityExecutableSearchDriveType.Fixed or
					AntigravityExecutableSearchDriveType.RamDisk;
			}

			return false;
		}
		catch
		{
			return false;
		}
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

	private async Task<SupportedBuild?> FindSupportedBuildAsync(
		IProgress<AntigravityMachineSetupProgress>? progress,
		CancellationToken cancellationToken)
	{
		progress?.Report(new AntigravityMachineSetupProgress(
			AntigravityMachineSetupStage.DiscoveringExecutable));
		AntigravityApprovedSource? approvedSource =
			_approvedSourceResolver.Resolve();
		IReadOnlyList<string> discoveredCandidates =
			await _discoverExecutableCandidatesAsync(cancellationToken)
				.ConfigureAwait(false);
		HashSet<string> executableCandidates = new(
			discoveredCandidates,
			StringComparer.OrdinalIgnoreCase);
		if (approvedSource is not null)
		{
			TryAddExecutableCandidate(
				executableCandidates,
				approvedSource.Path,
				File.Exists,
				GetExecutableSearchDriveType);
		}

		List<SupportedBuild> matches = new();
		foreach (string executablePath in executableCandidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			AntigravityOfficialPrintCapabilityValidationResult result;
			try
			{
				result = await _validateOfficialPrintCapabilityAsync(
						executablePath,
						cancellationToken)
					.ConfigureAwait(false) ??
					throw new InvalidOperationException(
						"The official AGY capability validator returned no result.");
			}
			catch (AntigravityOfficialPrintExecutionGateException exception)
			{
				throw new SetupFailureException(
					exception.FailureKind ==
					AntigravityOfficialPrintExecutionGateFailureKind
						.ContentionTimedOut
						? AntigravityMachineSetupFailureKind.ExecutionBusy
						: AntigravityMachineSetupFailureKind.UnexpectedFailure,
					exception);
			}

			using (result)
			{
				if (result.IsSupported &&
					!string.IsNullOrWhiteSpace(result.CliVersion) &&
					(result.ExecutableLease is not null) &&
					!result.ExecutableLease.IsDisposed)
				{
					matches.Add(new SupportedBuild(
						result.CliVersion,
						result.ExecutableLease.AbsolutePath));
				}
			}
		}

		return matches.Count switch
		{
			0 => null,
			1 => matches[0],
			_ => throw new SetupFailureException(
				AntigravityMachineSetupFailureKind.AmbiguousExecutable)
		};
	}

	private async Task<AntigravityMachineSetupPreparationResult>
		PrepareOfficialPrintBuildAsync(
			SupportedBuild supportedBuild,
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
					cancellationToken).ConfigureAwait(false)
				: await _officialPrintUsageClient.CaptureAsync(
					supportedBuild.ExecutablePath,
					cancellationToken).ConfigureAwait(false);
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
				return PreviewFailure(preview, revalidateSafetyAsync);
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
				() => ValueTask.CompletedTask);
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
					cancellationToken)
				.ConfigureAwait(false) ??
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

			ApplyApprovedSourceChange(
				new AntigravityApprovedSource(
					AntigravityMachineSetupSourceKind.OfficialPrint,
					executablePath),
				_approvedSourceStore,
				cancellationToken);
		}
	}

	private static AntigravityMachineSetupPreparationResult Failure(
		AntigravityMachineSetupFailureKind failureKind)
	{
		return new AntigravityMachineSetupPreparationResult(failureKind);
	}

	private static AntigravityMachineSetupPreparationResult PreviewFailure(
		AntigravityProductionUsageResult preview,
		Func<
			CancellationToken,
			Task<AntigravityMachineSetupPreparationResult>>?
			revalidateSafetyAsync)
	{
		AntigravityMachineSetupFailureKind failureKind =
			preview.FailureKind switch
			{
				AntigravityProductionUsageFailureKind.ExecutionBusy =>
					AntigravityMachineSetupFailureKind.ExecutionBusy,
				AntigravityProductionUsageFailureKind.UsageShapeRejected =>
					AntigravityMachineSetupFailureKind.UsageRejected,
				AntigravityProductionUsageFailureKind.SafetyLatched when
					!preview.AutomaticRevalidationPending =>
					AntigravityMachineSetupFailureKind
						.SafetyRevalidationRequired,
				_ => AntigravityMachineSetupFailureKind.UnexpectedFailure
			};

		return new AntigravityMachineSetupPreparationResult(
			failureKind,
			AntigravityMachineSetupSourceKind.OfficialPrint,
			preview,
			revalidateSafetyAsync);
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

	private static void EnsureApprovedSourcesEqual(
		AntigravityApprovedSource? observed,
		AntigravityApprovedSource? expected)
	{
		if (((observed is null) != (expected is null)) ||
			((observed is not null) &&
				((observed.SourceKind != expected!.SourceKind) ||
				 !string.Equals(
					 observed.Path,
					 expected.Path,
					 StringComparison.OrdinalIgnoreCase))))
		{
			throw new InvalidOperationException(
				"The approved AGY source setting could not be verified.");
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
		}

		return candidates.OrderBy(
			candidate => candidate,
			StringComparer.OrdinalIgnoreCase);
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
		}
		finally
		{
			lock (ExecutableDiscoverySyncRoot)
			{
				if (ReferenceEquals(_activeExecutableDiscovery, operation))
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
