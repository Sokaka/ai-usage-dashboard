using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;

using AiUsageDashboard.App.Providers;

using GitHub.Copilot;

namespace AiUsageDashboard.Tests;

public sealed class CopilotCliAccountConnectorTests
{
	private sealed class FakeCredentialStore : ICopilotCredentialStore
	{
		private readonly Dictionary<Guid, CopilotStoredCredential> _active = new();
		private readonly Dictionary<Guid, CopilotStoredCredential> _pending = new();
		private readonly object _sync = new();

		internal int CommitCount { get; private set; }

		internal int DiscardCount { get; private set; }

		internal int StageCount { get; private set; }

		public CopilotStoredCredential? Read(Guid accountId)
		{
			lock (_sync)
			{
				return _active.GetValueOrDefault(accountId);
			}
		}

		public void Stage(Guid accountId, CopilotStoredCredential credential)
		{
			ArgumentNullException.ThrowIfNull(credential);
			lock (_sync)
			{
				StageCount++;
				_pending[accountId] = credential;
			}
		}

		public bool CommitStaged(
			Guid accountId,
			string expectedProviderAccountIdentity)
		{
			lock (_sync)
			{
				CommitCount++;
				if (!_pending.TryGetValue(
						accountId,
						out CopilotStoredCredential? credential))
				{
					return false;
				}

				if (!CopilotAccountIdentityRules.AreEquivalent(
						credential.ProviderAccountIdentity,
						expectedProviderAccountIdentity))
				{
					throw new CopilotClientException(
						CopilotFailureKind.AccountMismatch,
						"Synthetic staged identity mismatch.");
				}

				_active[accountId] = credential;
				_pending.Remove(accountId);
				return true;
			}
		}

		public void RecoverStaged(
			Guid accountId,
			string? expectedProviderAccountIdentity)
		{
			lock (_sync)
			{
				if (!_pending.TryGetValue(
						accountId,
						out CopilotStoredCredential? credential))
				{
					return;
				}

				if (CopilotAccountIdentityRules.AreEquivalent(
						credential.ProviderAccountIdentity,
						expectedProviderAccountIdentity))
				{
					_active[accountId] = credential;
				}

				_pending.Remove(accountId);
			}
		}

		public void DiscardStaged(Guid accountId)
		{
			lock (_sync)
			{
				DiscardCount++;
				_pending.Remove(accountId);
			}
		}

		public void Delete(Guid accountId)
		{
			lock (_sync)
			{
				_active.Remove(accountId);
				_pending.Remove(accountId);
			}
		}

		internal CopilotStoredCredential? ReadPending(Guid accountId)
		{
			lock (_sync)
			{
				return _pending.GetValueOrDefault(accountId);
			}
		}

		internal void SeedActive(
			Guid accountId,
			CopilotStoredCredential credential)
		{
			lock (_sync)
			{
				_active[accountId] = credential;
			}
		}
	}

	private sealed class FakeGitHubUserClient : ICopilotGitHubUserClient
	{
		private readonly IReadOnlyDictionary<string, CopilotAccountIdentity>
			_principalsByCredential;

		internal FakeGitHubUserClient(
			IReadOnlyDictionary<string, CopilotAccountIdentity>
				principalsByCredential)
		{
			_principalsByCredential = principalsByCredential;
		}

		public Task<CopilotAccountIdentity> GetAuthenticatedUserAsync(
			string host,
			string accessToken,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(_principalsByCredential[accessToken]);
		}
	}

	private sealed class FakeSdkClient : ICopilotSdkClient
	{
		private static readonly IReadOnlyDictionary<string, CopilotSdkQuotaValue>
			Quota = new Dictionary<string, CopilotSdkQuotaValue>(
				StringComparer.Ordinal)
			{
				["premium_interactions"] = new CopilotSdkQuotaValue(
					100,
					10,
					90,
					DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
					IsUnlimitedEntitlement: false,
					OverageAllowedWithExhaustedQuota: false,
					UsageAllowedWithExhaustedQuota: false)
			};

		public Task StartAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}

		public Task<CopilotSdkSubscriptionMetadata?>
			GetSubscriptionMetadataAsync(
			string expectedHost,
			string expectedLogin,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<CopilotSdkSubscriptionMetadata?>(new(
				"pro",
				IsTokenBasedBilling: false));
		}

		public Task<IReadOnlyDictionary<string, CopilotSdkQuotaValue>> GetQuotaAsync(
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(Quota);
		}

		public Task StopAsync()
		{
			return Task.CompletedTask;
		}

		public Task ForceStopAsync()
		{
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeBootstrapClient : ICopilotBootstrapClient
	{
		private readonly Task _disposeTask;
		private readonly Action _startHandler;
		private readonly CopilotBootstrapCredential _credential;

		internal FakeBootstrapClient(
			CopilotBootstrapCredential credential,
			Action startHandler,
			Task? disposeTask = null)
		{
			_credential = credential;
			_startHandler = startHandler;
			_disposeTask = disposeTask ?? Task.CompletedTask;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_startHandler();
			return Task.CompletedTask;
		}

		public Task<CopilotBootstrapCredential> GetSelectedCredentialAsync(
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(_credential);
		}

		public Task StopAsync()
		{
			return Task.CompletedTask;
		}

		public Task ForceStopAsync()
		{
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			return new ValueTask(_disposeTask);
		}
	}

	private sealed class FakeLoginProcess : ICopilotLoginProcess
	{
		private readonly TaskCompletionSource<bool>? _releaseSource;
		private readonly bool _throwOnKill;
		private int _hasExited;
		private int _killCount;
		private int _waitCallCount;

		public bool HasExited => Volatile.Read(ref _hasExited) != 0;

		public int ExitCode { get; }

		internal int KillCount => Volatile.Read(ref _killCount);

		internal int WaitCallCount => Volatile.Read(ref _waitCallCount);

		internal TaskCompletionSource<bool> WaitStarted { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal FakeLoginProcess(
			bool completeImmediately,
			int exitCode = 0,
			bool throwOnKill = false)
		{
			ExitCode = exitCode;
			_throwOnKill = throwOnKill;
			if (completeImmediately)
			{
				_hasExited = 1;
			}
			else
			{
				_releaseSource = new TaskCompletionSource<bool>(
					TaskCreationOptions.RunContinuationsAsynchronously);
			}
		}

		public async Task WaitForExitAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _waitCallCount);
			WaitStarted.TrySetResult(true);
			if (HasExited)
			{
				return;
			}

			await _releaseSource!.Task.WaitAsync(cancellationToken);
		}

		public void KillEntireProcessTree()
		{
			Interlocked.Increment(ref _killCount);
			if (_throwOnKill)
			{
				throw new InvalidOperationException(
					"Synthetic process termination failure.");
			}

			CompleteExit();
		}

		public void Dispose()
		{
		}

		internal void CompleteExit()
		{
			Interlocked.Exchange(ref _hasExited, 1);
			_releaseSource?.TrySetResult(true);
		}
	}

	private sealed class ConnectorHarness
	{
		private readonly ConcurrentQueue<CopilotBootstrapCredential>
			_bootstrapCredentials;
		private readonly Func<FakeLoginProcess> _loginProcessFactory;
		private int _bootstrapStartCount;
		private int _loginStartCount;

		internal CopilotCliAccountConnector Connector { get; }

		internal FakeCredentialStore CredentialStore { get; } = new();

		internal int BootstrapStartCount => Volatile.Read(ref _bootstrapStartCount);

		internal int LoginStartCount => Volatile.Read(ref _loginStartCount);

		internal ConcurrentQueue<ProcessStartInfo> LoginStartInfos { get; } = new();

		internal ConnectorHarness(
			string testRoot,
			IReadOnlyCollection<(
				CopilotAccountIdentity Principal,
				string Credential)> attempts,
			Func<FakeLoginProcess>? loginProcessFactory = null,
			Func<
				CopilotBootstrapCredential,
				Action,
				ICopilotBootstrapClient>? bootstrapClientFactory = null,
			TimeSpan? processTerminationTimeout = null,
			Action<ProcessStartInfo>? loginStartHandler = null,
			Func<ProcessStartInfo, ICopilotLoginProcess>? processStarter = null,
			Action<string>? bootstrapHomeHandler = null)
		{
			_loginProcessFactory = loginProcessFactory ??
				(() => new FakeLoginProcess(completeImmediately: true));
			_bootstrapCredentials = new ConcurrentQueue<CopilotBootstrapCredential>(
				attempts.Select(attempt => new CopilotBootstrapCredential(
					attempt.Principal.Host,
					attempt.Principal.Login,
					attempt.Credential)));
			Dictionary<string, CopilotAccountIdentity> principalsByCredential =
				attempts.ToDictionary(
					attempt => attempt.Credential,
					attempt => attempt.Principal,
					StringComparer.Ordinal);
			CopilotAccountOperationGate operationGate = new();
			CopilotSdkQuotaClient quotaClient = new(
				accountId => GetHomeDirectory(testRoot, accountId),
				operationGate,
				CredentialStore,
				new FakeGitHubUserClient(principalsByCredential),
				static (_, _) => new FakeSdkClient(),
				utcNow: () => DateTimeOffset.Parse("2026-09-01T12:00:00Z"),
				operationTimeout: TimeSpan.FromSeconds(5),
				cleanupTimeout: TimeSpan.FromSeconds(2),
				planProbeTimeout: TimeSpan.FromSeconds(2));
			string executablePath = Path.Combine(testRoot, "runtime", "copilot.exe");
			Connector = new CopilotCliAccountConnector(
				accountId => GetHomeDirectory(testRoot, accountId),
				operationGate,
				CredentialStore,
				quotaClient,
				() => executablePath,
				startInfo =>
				{
					loginStartHandler?.Invoke(startInfo);
					Interlocked.Increment(ref _loginStartCount);
					LoginStartInfos.Enqueue(startInfo);
					return processStarter is null
						? _loginProcessFactory()
						: processStarter(startInfo);
				},
				bootstrapHomeDirectory =>
				{
					bootstrapHomeHandler?.Invoke(bootstrapHomeDirectory);
					if (!_bootstrapCredentials.TryDequeue(
							out CopilotBootstrapCredential? credential))
					{
						throw new InvalidOperationException(
							"No synthetic bootstrap credential remains.");
					}

					Action startHandler = () => Interlocked.Increment(
						ref _bootstrapStartCount);
					return bootstrapClientFactory is null
						? new FakeBootstrapClient(credential, startHandler)
						: bootstrapClientFactory(credential, startHandler);
				},
				processTerminationTimeout);
		}
	}

	[Fact]
	public void BootstrapSafetyBounds_CoverObservedOfficialCliShape()
	{
		Assert.True(
			CopilotCliAccountConnector.MaximumBootstrapTreeEntryCount >= 1460);
		Assert.True(
			CopilotCliAccountConnector.MaximumBootstrapFileBytes >= 116_732_192);
		Assert.True(
			CopilotCliAccountConnector.MaximumBootstrapTotalBytes >= 223_391_149);
	}

	[Fact]
	public async Task Candidate_StagesThenCommitsAccountScopedCredential()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_STAGE_COMMIT",
			"stage-user",
			1001);
		const string fixtureCredential = "fixture-credential-stage";
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[] { (principal, fixtureCredential) });

		await using ICopilotConnectionCandidate candidate =
			await harness.Connector.BeginConnectAsync(
				accountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false);

		string expectedIdentity = CopilotAccountIdentityRules.Create(principal);
		Assert.Equal(principal, candidate.UsageReport.Account);
		Assert.Null(harness.CredentialStore.Read(accountId));
		Assert.Equal(
			expectedIdentity,
			harness.CredentialStore.ReadPending(accountId)?
				.ProviderAccountIdentity);
		Assert.Equal(1, harness.CredentialStore.StageCount);
		Assert.Equal(0, harness.CredentialStore.CommitCount);

		await candidate.CommitAsync();

		CopilotStoredCredential committed = Assert.IsType<CopilotStoredCredential>(
			harness.CredentialStore.Read(accountId));
		Assert.Equal(expectedIdentity, committed.ProviderAccountIdentity);
		Assert.Equal(fixtureCredential, committed.AccessToken);
		Assert.Null(harness.CredentialStore.ReadPending(accountId));
		Assert.Equal(1, harness.CredentialStore.CommitCount);
		Assert.Equal(0, harness.CredentialStore.DiscardCount);
	}

	[Fact]
	public async Task BeginConnectAsync_WhenTokenSpansScanBufferBoundary_RejectsPersistedCredential()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_TOKEN_BOUNDARY",
			"token-boundary-user",
			1001);
		const string fixtureCredential = "fixture-credential-token-boundary";
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[] { (principal, fixtureCredential) },
			bootstrapHomeHandler: bootstrapHomeDirectory =>
			{
				File.WriteAllText(
					Path.Combine(bootstrapHomeDirectory, "credential-cache.bin"),
					new string('x', (64 * 1024) - 5) + fixtureCredential);
			});

		CopilotAccountLoginException failure =
			await Assert.ThrowsAsync<CopilotAccountLoginException>(() =>
				harness.Connector.BeginConnectAsync(
					accountId,
					expectedProviderAccountIdentity: null,
					allowAccountSwitch: false));

		Assert.Equal(
			CopilotAccountLoginFailureKind.AuthenticationFailed,
			failure.Kind);
		Assert.Contains("受保護的 credential store", failure.Message);
		Assert.Equal(1, harness.LoginStartCount);
		Assert.Equal(1, harness.BootstrapStartCount);
	}

	[Fact]
	public async Task BeginConnectAsync_WithExpectedInternetCacheJunctionInActiveAttempt_CompletesAndCleansIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_ACTIVE_WINDOWS_LINK",
			"active-windows-link-user",
			1001);
		string? bootstrapAttempt = null;
		string? internetCacheLink = null;
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(principal, "fixture-credential-active-windows-link")
			},
			bootstrapHomeHandler: bootstrapHomeDirectory =>
			{
				bootstrapAttempt = bootstrapHomeDirectory;
				string internetCacheTarget = GetInternetCachePath(
					bootstrapHomeDirectory,
					"IE");
				internetCacheLink = GetInternetCachePath(
					bootstrapHomeDirectory,
					"Content.IE5");
				Directory.CreateDirectory(internetCacheTarget);
				File.WriteAllText(
					Path.Combine(internetCacheTarget, "cache-entry.txt"),
					"synthetic cache entry");
				JunctionTestHelper.CreateAsync(
					internetCacheLink,
					internetCacheTarget).GetAwaiter().GetResult();
				File.SetAttributes(
					internetCacheTarget,
					File.GetAttributes(internetCacheTarget) |
						FileAttributes.Hidden |
						FileAttributes.System |
						FileAttributes.NotContentIndexed);
				File.SetAttributes(
					internetCacheLink,
					File.GetAttributes(internetCacheLink) |
						FileAttributes.Hidden |
						FileAttributes.System |
						FileAttributes.NotContentIndexed);
			});

		try
		{
			await using ICopilotConnectionCandidate candidate =
				await harness.Connector.BeginConnectAsync(
					accountId,
					expectedProviderAccountIdentity: null,
					allowAccountSwitch: false);

			Assert.NotNull(bootstrapAttempt);
			Assert.False(Directory.Exists(bootstrapAttempt));
			Assert.Equal(1, harness.LoginStartCount);
			Assert.Equal(1, harness.BootstrapStartCount);
		}
		finally
		{
			if (internetCacheLink is not null)
			{
				JunctionTestHelper.Delete(internetCacheLink);
			}
		}
	}

	[Fact]
	public async Task Candidate_DisposeWithoutCommit_DiscardsPendingAndPreservesActiveCredential()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity previousPrincipal = CreatePrincipal(
			"NODE_PREVIOUS",
			"previous-user",
			1001);
		CopilotAccountIdentity nextPrincipal = CreatePrincipal(
			"NODE_NEXT",
			"next-user",
			1002);
		CopilotStoredCredential previousCredential = new(
			CopilotAccountIdentityRules.Create(previousPrincipal),
			"fixture-credential-previous");
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[] { (nextPrincipal, "fixture-credential-next") });
		harness.CredentialStore.SeedActive(accountId, previousCredential);

		ICopilotConnectionCandidate candidate =
			await harness.Connector.BeginConnectAsync(
				accountId,
				previousCredential.ProviderAccountIdentity,
				allowAccountSwitch: true);
		Assert.NotNull(harness.CredentialStore.ReadPending(accountId));

		await candidate.DisposeAsync();

		Assert.Same(previousCredential, harness.CredentialStore.Read(accountId));
		Assert.Null(harness.CredentialStore.ReadPending(accountId));
		Assert.Equal(0, harness.CredentialStore.CommitCount);
		Assert.Equal(1, harness.CredentialStore.DiscardCount);
	}

	[Fact]
	public async Task BeginConnectAsync_WhenLoginIsCancelled_WritesNoCredential()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_CANCEL",
			"cancel-user",
			1001);
		FakeLoginProcess process = new(completeImmediately: false);
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[] { (principal, "fixture-credential-cancel") },
			() => process);
		using CancellationTokenSource cancellationSource = new();
		Task<ICopilotConnectionCandidate> connectTask =
			harness.Connector.BeginConnectAsync(
				accountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false,
				cancellationSource.Token);
		await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		cancellationSource.Cancel();

		CopilotAccountLoginException failure =
			await Assert.ThrowsAsync<CopilotAccountLoginException>(
				async () => await connectTask);
		Assert.Equal(CopilotAccountLoginFailureKind.Cancelled, failure.Kind);
		Assert.Equal(1, process.KillCount);
		Assert.Null(harness.CredentialStore.Read(accountId));
		Assert.Null(harness.CredentialStore.ReadPending(accountId));
		Assert.Equal(0, harness.CredentialStore.StageCount);
		Assert.Equal(0, harness.CredentialStore.CommitCount);
	}

	[Fact]
	public async Task BeginConnectAsync_WhenPrincipalMismatchesWithoutSwitch_WritesNoNewCredential()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity expectedPrincipal = CreatePrincipal(
			"NODE_EXPECTED",
			"expected-user",
			1001);
		CopilotAccountIdentity observedPrincipal = CreatePrincipal(
			"NODE_OBSERVED",
			"observed-user",
			1002);
		CopilotStoredCredential activeCredential = new(
			CopilotAccountIdentityRules.Create(expectedPrincipal),
			"fixture-credential-active");
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[] { (observedPrincipal, "fixture-credential-observed") });
		harness.CredentialStore.SeedActive(accountId, activeCredential);

		CopilotClientException failure =
			await Assert.ThrowsAsync<CopilotClientException>(() =>
				harness.Connector.BeginConnectAsync(
					accountId,
					activeCredential.ProviderAccountIdentity,
					allowAccountSwitch: false));

		Assert.Equal(CopilotFailureKind.AccountMismatch, failure.Kind);
		Assert.Same(activeCredential, harness.CredentialStore.Read(accountId));
		Assert.Null(harness.CredentialStore.ReadPending(accountId));
		Assert.Equal(0, harness.CredentialStore.StageCount);
		Assert.Equal(0, harness.CredentialStore.CommitCount);
	}

	[Fact]
	public async Task BeginConnectAsync_ForDifferentAccounts_SerializesGlobalInteractiveLoginAdmission()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		CopilotAccountIdentity firstPrincipal = CreatePrincipal(
			"NODE_FIRST",
			"first-user",
			1001);
		CopilotAccountIdentity secondPrincipal = CreatePrincipal(
			"NODE_SECOND",
			"second-user",
			1002);
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(firstPrincipal, "fixture-credential-first"),
				(secondPrincipal, "fixture-credential-second")
			});

		ICopilotConnectionCandidate firstCandidate =
			await harness.Connector.BeginConnectAsync(
				firstAccountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false);
		Task<ICopilotConnectionCandidate> secondConnectTask =
			harness.Connector.BeginConnectAsync(
				secondAccountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false);

		await Task.Delay(TimeSpan.FromMilliseconds(250));
		Assert.False(secondConnectTask.IsCompleted);
		Assert.Equal(1, harness.LoginStartCount);
		Assert.Equal(1, harness.BootstrapStartCount);

		await firstCandidate.CommitAsync();
		await firstCandidate.DisposeAsync();
		ICopilotConnectionCandidate secondCandidate =
			await secondConnectTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(2, harness.LoginStartCount);
		Assert.Equal(2, harness.BootstrapStartCount);
		await secondCandidate.DisposeAsync();
	}

	[Fact]
	public async Task BeginConnectAsync_WhenCancelledProcessExitIsLate_KeepsGlobalAdmissionUntilExit()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		CopilotAccountIdentity secondPrincipal = CreatePrincipal(
			"NODE_LATE_PROCESS_SECOND",
			"late-process-second-user",
			1002);
		FakeLoginProcess lateProcess = new(
			completeImmediately: false,
			throwOnKill: true);
		ConcurrentQueue<FakeLoginProcess> processes = new(
			new[]
			{
				lateProcess,
				new FakeLoginProcess(completeImmediately: true)
			});
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(secondPrincipal, "fixture-credential-late-process-second")
			},
			() => processes.TryDequeue(out FakeLoginProcess? process)
				? process
				: throw new InvalidOperationException(
					"No synthetic login process remains."),
			processTerminationTimeout: TimeSpan.FromMilliseconds(50));
		using CancellationTokenSource cancellationSource = new();
		Task<ICopilotConnectionCandidate> firstConnectTask =
			harness.Connector.BeginConnectAsync(
				firstAccountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false,
				cancellationSource.Token);
		await lateProcess.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Task<ICopilotConnectionCandidate> secondConnectTask =
			harness.Connector.BeginConnectAsync(
				secondAccountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false);

		cancellationSource.Cancel();

		await Assert.ThrowsAsync<CopilotAccountLoginException>(async () =>
			await firstConnectTask.WaitAsync(TimeSpan.FromSeconds(20)));
		await Task.Delay(TimeSpan.FromMilliseconds(250));
		Assert.Equal(1, lateProcess.KillCount);
		Assert.True(lateProcess.WaitCallCount >= 2);
		Assert.False(secondConnectTask.IsCompleted);
		Assert.Equal(1, harness.LoginStartCount);

		lateProcess.CompleteExit();

		ICopilotConnectionCandidate secondCandidate =
			await secondConnectTask.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(2, harness.LoginStartCount);
		await secondCandidate.DisposeAsync();
	}

	[Fact]
	public async Task BeginConnectAsync_WhenInteractiveLaunchTreeIsUnknown_KeepsGlobalLockUntilPositiveEmpty()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_UNKNOWN_INTERACTIVE_TREE",
			"unknown-interactive-tree-user",
			1001);
		TaskCompletionSource<bool> containmentCompletionSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		const string privateDiagnostic =
			"synthetic private interactive containment detail";
		FieldInfo latchField = typeof(CopilotCliAccountConnector).GetField(
				"_lateCleanupFailureMessage",
				BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException(
				"Copilot containment latch field is unavailable.");
		object? previousLatchValue = latchField.GetValue(null);
		ConnectorHarness? harness = null;
		int baselineLateCleanupCount = 0;

		try
		{
			latchField.SetValue(null, null);
			harness = new ConnectorHarness(
				temporaryDirectory.Path,
				new[]
				{
					(principal, "fixture-credential-unknown-interactive-tree")
				},
				processStarter: startInfo =>
					CopilotCliAccountConnector.StartContainedInteractiveProcess(
						startInfo,
						(_, _) =>
							throw new WindowsJobContainedProcessLaunchException(
								privateDiagnostic,
								isLaunchBlocked: false,
								isTreeEmptyConfirmed: false,
								containmentCompletionSource.Task,
								new InvalidOperationException(privateDiagnostic))));
			baselineLateCleanupCount = harness.Connector.ActiveLateCleanupCount;

			CopilotAccountLoginException failure =
				await Assert.ThrowsAsync<CopilotAccountLoginException>(() =>
					harness.Connector.BeginConnectAsync(
						accountId,
						expectedProviderAccountIdentity: null,
						allowAccountSwitch: false));

			Assert.Equal(
				CopilotAccountLoginFailureKind.ProcessFailed,
				failure.Kind);
			Assert.Contains(
				"重新啟動 AI Usage",
				failure.Message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				privateDiagnostic,
				failure.ToString(),
				StringComparison.Ordinal);
			Assert.Equal(1, harness.LoginStartCount);
			Assert.Equal(0, harness.BootstrapStartCount);
			Assert.Equal(
				baselineLateCleanupCount + 1,
				harness.Connector.ActiveLateCleanupCount);
			string globalLockPath = Path.Combine(
				temporaryDirectory.Path,
				"copilot",
				"interactive-login-v1.lock");
			Assert.Throws<IOException>(() =>
			{
				using FileStream blockedStream = new(
					globalLockPath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None);
			});

			containmentCompletionSource.TrySetResult(true);
			await WaitUntilAsync(
				() => harness.Connector.ActiveLateCleanupCount ==
					baselineLateCleanupCount,
				TimeSpan.FromSeconds(5));
			using FileStream acquiredStream = new(
				globalLockPath,
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None);
		}
		finally
		{
			containmentCompletionSource.TrySetResult(true);
			if (harness is not null)
			{
				try
				{
					await WaitUntilAsync(
						() => harness.Connector.ActiveLateCleanupCount <=
							baselineLateCleanupCount,
						TimeSpan.FromSeconds(5));
				}
				catch
				{
				}
			}

			latchField.SetValue(null, previousLatchValue);
		}
	}

	[Fact]
	public async Task BeginConnectAsync_WhenBootstrapDisposeIsLate_KeepsGlobalAdmissionUntilCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		CopilotAccountIdentity firstPrincipal = CreatePrincipal(
			"NODE_LATE_BOOTSTRAP_FIRST",
			"late-bootstrap-first-user",
			1001);
		CopilotAccountIdentity secondPrincipal = CreatePrincipal(
			"NODE_LATE_BOOTSTRAP_SECOND",
			"late-bootstrap-second-user",
			1002);
		TaskCompletionSource<bool> disposeSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int bootstrapClientCount = 0;
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(firstPrincipal, "fixture-credential-late-bootstrap-first"),
				(secondPrincipal, "fixture-credential-late-bootstrap-second")
			},
			bootstrapClientFactory: (credential, startHandler) =>
				Interlocked.Increment(ref bootstrapClientCount) == 1
					? new FakeBootstrapClient(
						credential,
						startHandler,
						disposeSource.Task)
					: new FakeBootstrapClient(credential, startHandler));
		Task<ICopilotConnectionCandidate> firstConnectTask =
			harness.Connector.BeginConnectAsync(
				firstAccountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false);
		await WaitUntilAsync(
			() => harness.BootstrapStartCount == 1,
			TimeSpan.FromSeconds(5));
		Task<ICopilotConnectionCandidate> secondConnectTask =
			harness.Connector.BeginConnectAsync(
				secondAccountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false);

		await Assert.ThrowsAsync<CopilotAccountLoginException>(async () =>
			await firstConnectTask.WaitAsync(TimeSpan.FromSeconds(20)));
		await Task.Delay(TimeSpan.FromMilliseconds(250));
		Assert.False(secondConnectTask.IsCompleted);
		Assert.Equal(1, harness.LoginStartCount);
		Assert.Equal(0, harness.CredentialStore.StageCount);

		disposeSource.TrySetResult(true);

		ICopilotConnectionCandidate secondCandidate =
			await secondConnectTask.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(2, harness.LoginStartCount);
		await secondCandidate.DisposeAsync();
	}

	[Fact]
	public async Task BeginConnectAsync_WhenBootstrapLaunchTreeIsUnknown_KeepsGlobalLockUntilPositiveEmpty()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_UNKNOWN_BOOTSTRAP_TREE",
			"unknown-bootstrap-tree-user",
			1001);
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"runtime",
			"copilot.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		TaskCompletionSource<bool> containmentCompletionSource = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		const string privateDiagnostic =
			"synthetic private failed-start containment detail";
		FieldInfo latchField = typeof(CopilotCliAccountConnector).GetField(
				"_lateCleanupFailureMessage",
				BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException(
				"Copilot containment latch field is unavailable.");
		object? previousLatchValue = latchField.GetValue(null);
		ConnectorHarness? harness = null;
		int baselineLateCleanupCount = 0;

		try
		{
			latchField.SetValue(null, null);
			harness = new ConnectorHarness(
				temporaryDirectory.Path,
				new[]
				{
					(principal, "fixture-credential-unknown-bootstrap-tree")
				},
				bootstrapClientFactory: (_, startHandler) =>
					new CopilotCliAccountConnector.BootstrapClient(
						executablePath,
						Path.Combine(
							temporaryDirectory.Path,
							"bootstrap-runtime-home"),
						(_, _) =>
						{
							startHandler();
							throw new WindowsJobContainedProcessLaunchException(
								privateDiagnostic,
								isLaunchBlocked: false,
								isTreeEmptyConfirmed: false,
								containmentCompletionSource.Task,
								new InvalidOperationException(privateDiagnostic));
						}));
			baselineLateCleanupCount = harness.Connector.ActiveLateCleanupCount;

			CopilotAccountLoginException failure =
				await Assert.ThrowsAsync<CopilotAccountLoginException>(async () =>
					await harness.Connector.BeginConnectAsync(
						accountId,
						expectedProviderAccountIdentity: null,
						allowAccountSwitch: false)
					.WaitAsync(TimeSpan.FromSeconds(20)));

			Assert.Equal(
				CopilotAccountLoginFailureKind.ProcessFailed,
				failure.Kind);
			Assert.Contains(
				"重新啟動 AI Usage",
				failure.Message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				privateDiagnostic,
				failure.ToString(),
				StringComparison.Ordinal);
			Assert.Equal(
				baselineLateCleanupCount + 1,
				harness.Connector.ActiveLateCleanupCount);
			string globalLockPath = Path.Combine(
				temporaryDirectory.Path,
				"copilot",
				"interactive-login-v1.lock");
			Assert.Throws<IOException>(() =>
			{
				using FileStream blockedStream = new(
					globalLockPath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None);
			});

			containmentCompletionSource.TrySetResult(true);
			await WaitUntilAsync(
				() => harness.Connector.ActiveLateCleanupCount ==
					baselineLateCleanupCount,
				TimeSpan.FromSeconds(5));
			using FileStream acquiredStream = new(
				globalLockPath,
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None);
		}
		finally
		{
			containmentCompletionSource.TrySetResult(true);
			if (harness is not null)
			{
				try
				{
					await WaitUntilAsync(
						() => harness.Connector.ActiveLateCleanupCount <=
							baselineLateCleanupCount,
						TimeSpan.FromSeconds(5));
				}
				catch
				{
				}
			}

			latchField.SetValue(null, previousLatchValue);
		}
	}

	[Fact]
	public async Task BeginConnectAsync_WithOrphanedBootstrapContext_RemovesItBeforeLoginStarts()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_ORPHAN_SWEEP",
			"orphan-sweep-user",
			1001);
		string orphanDirectory = Path.Combine(
			GetBootstrapRoot(temporaryDirectory.Path, accountId),
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(orphanDirectory);
		File.WriteAllText(
			Path.Combine(orphanDirectory, "credential.txt"),
			"synthetic orphaned credential material");
		bool wasOrphanAbsentAtLoginStart = false;
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(principal, "fixture-credential-orphan-sweep")
			},
			loginStartHandler: startInfo =>
			{
				Assert.True(Directory.Exists(startInfo.WorkingDirectory));
				wasOrphanAbsentAtLoginStart = !Directory.Exists(
					orphanDirectory);
			});

		await using ICopilotConnectionCandidate candidate =
			await harness.Connector.BeginConnectAsync(
				accountId,
				expectedProviderAccountIdentity: null,
				allowAccountSwitch: false);

		Assert.True(wasOrphanAbsentAtLoginStart);
		Assert.False(Directory.Exists(orphanDirectory));
		Assert.Equal(1, harness.LoginStartCount);
	}

	[Fact]
	public async Task BeginConnectAsync_WithOfficialWindowsBootstrapShape_RemovesInternetCacheJunctionAsLeaf()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_WINDOWS_ORPHAN_SWEEP",
			"windows-orphan-sweep-user",
			1001);
		string orphanDirectory = Path.Combine(
			GetBootstrapRoot(temporaryDirectory.Path, accountId),
			Guid.NewGuid().ToString("N"));
		string internetCacheTarget = GetInternetCachePath(
			orphanDirectory,
			"IE");
		string internetCacheLink = GetInternetCachePath(
			orphanDirectory,
			"Content.IE5");
		Directory.CreateDirectory(internetCacheTarget);
		await JunctionTestHelper.CreateAsync(
			internetCacheLink,
			internetCacheTarget);
		File.SetAttributes(
			internetCacheTarget,
			File.GetAttributes(internetCacheTarget) |
				FileAttributes.Hidden |
				FileAttributes.System |
				FileAttributes.NotContentIndexed);
		File.SetAttributes(
			internetCacheLink,
			File.GetAttributes(internetCacheLink) |
				FileAttributes.Hidden |
				FileAttributes.System |
				FileAttributes.NotContentIndexed);

		try
		{
			for (int index = 0; index < 520; index++)
			{
				File.Create(Path.Combine(
					orphanDirectory,
					$"official-entry-{index:D3}.tmp")).Dispose();
			}

			using (FileStream largePackageFile = File.Create(Path.Combine(
				orphanDirectory,
				"official-package.bin")))
			{
				largePackageFile.SetLength(9L * 1024 * 1024);
			}

			bool wasOrphanAbsentAtLoginStart = false;
			ConnectorHarness harness = new(
				temporaryDirectory.Path,
				new[]
				{
					(principal, "fixture-credential-windows-orphan-sweep")
				},
				loginStartHandler: _ =>
				{
					wasOrphanAbsentAtLoginStart = !Directory.Exists(
						orphanDirectory);
				});

			await using ICopilotConnectionCandidate candidate =
				await harness.Connector.BeginConnectAsync(
					accountId,
					expectedProviderAccountIdentity: null,
					allowAccountSwitch: false);

			Assert.True(wasOrphanAbsentAtLoginStart);
			Assert.False(Directory.Exists(orphanDirectory));
			Assert.Equal(1, harness.LoginStartCount);
		}
		finally
		{
			JunctionTestHelper.Delete(internetCacheLink);
		}
	}

	[Fact]
	public async Task BeginConnectAsync_WithInternetCacheJunctionOutsideAttempt_FailsClosedAndPreservesTarget()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_OUTSIDE_ORPHAN",
			"outside-orphan-user",
			1001);
		string orphanDirectory = Path.Combine(
			GetBootstrapRoot(temporaryDirectory.Path, accountId),
			Guid.NewGuid().ToString("N"));
		string internetCacheLink = GetInternetCachePath(
			orphanDirectory,
			"Content.IE5");
		string outsideTarget = Path.Combine(
			temporaryDirectory.Path,
			"outside-target");
		string outsideSentinel = Path.Combine(outsideTarget, "sentinel.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(internetCacheLink)!);
		Directory.CreateDirectory(outsideTarget);
		File.WriteAllText(outsideSentinel, "must remain");
		await JunctionTestHelper.CreateAsync(internetCacheLink, outsideTarget);
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(principal, "fixture-credential-outside-orphan")
			});

		try
		{
			CopilotAccountLoginException failure =
				await Assert.ThrowsAsync<CopilotAccountLoginException>(() =>
					harness.Connector.BeginConnectAsync(
						accountId,
						expectedProviderAccountIdentity: null,
						allowAccountSwitch: false));

			Assert.Equal(
				CopilotAccountLoginFailureKind.ProcessFailed,
				failure.Kind);
			Assert.True(File.Exists(outsideSentinel));
			Assert.True(Directory.Exists(orphanDirectory));
			Assert.Equal(0, harness.LoginStartCount);
			Assert.Equal(0, harness.BootstrapStartCount);
		}
		finally
		{
			JunctionTestHelper.Delete(internetCacheLink);
		}
	}

	[Fact]
	public async Task BeginConnectAsync_WithUnexpectedInRootJunction_FailsClosed()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_UNEXPECTED_LINK",
			"unexpected-link-user",
			1001);
		string orphanDirectory = Path.Combine(
			GetBootstrapRoot(temporaryDirectory.Path, accountId),
			Guid.NewGuid().ToString("N"));
		string targetDirectory = Path.Combine(orphanDirectory, "inside-target");
		string unexpectedLink = Path.Combine(orphanDirectory, "unexpected-link");
		Directory.CreateDirectory(targetDirectory);
		await JunctionTestHelper.CreateAsync(unexpectedLink, targetDirectory);
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(principal, "fixture-credential-unexpected-link")
			});

		try
		{
			await Assert.ThrowsAsync<CopilotAccountLoginException>(() =>
				harness.Connector.BeginConnectAsync(
					accountId,
					expectedProviderAccountIdentity: null,
					allowAccountSwitch: false));

			Assert.True(Directory.Exists(targetDirectory));
			Assert.Equal(0, harness.LoginStartCount);
			Assert.Equal(0, harness.BootstrapStartCount);
		}
		finally
		{
			JunctionTestHelper.Delete(unexpectedLink);
		}
	}

	[Fact]
	public async Task BeginConnectAsync_WithUnexpectedBootstrapRootEntry_FailsBeforeLoginStarts()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_INVALID_ORPHAN",
			"invalid-orphan-user",
			1001);
		string bootstrapRoot = GetBootstrapRoot(
			temporaryDirectory.Path,
			accountId);
		Directory.CreateDirectory(bootstrapRoot);
		File.WriteAllText(
			Path.Combine(bootstrapRoot, "unexpected.txt"),
			"synthetic unexpected bootstrap content");
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(principal, "fixture-credential-invalid-orphan")
			});

		CopilotAccountLoginException failure =
			await Assert.ThrowsAsync<CopilotAccountLoginException>(() =>
				harness.Connector.BeginConnectAsync(
					accountId,
					expectedProviderAccountIdentity: null,
					allowAccountSwitch: false));

		Assert.Equal(
			CopilotAccountLoginFailureKind.ProcessFailed,
			failure.Kind);
		Assert.Contains("未啟動新的登入", failure.Message, StringComparison.Ordinal);
		Assert.Null(failure.InnerException);
		Assert.Equal(0, harness.LoginStartCount);
		Assert.Equal(0, harness.BootstrapStartCount);
	}

	[Fact]
	public async Task BeginConnectAsync_WhenContainmentLatchIsSet_ReturnsSafeRestartFailureBeforeLoginStarts()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_CONTAINMENT_LATCH",
			"containment-latch-user",
			1001);
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(principal, "fixture-credential-containment-latch")
			});
		FieldInfo latchField = typeof(CopilotCliAccountConnector).GetField(
				"_lateCleanupFailureMessage",
				BindingFlags.NonPublic | BindingFlags.Static) ??
			throw new InvalidOperationException(
				"Copilot containment latch field is unavailable.");
		object? previousValue = latchField.GetValue(null);
		const string privateDiagnostic = "synthetic private containment detail";

		try
		{
			latchField.SetValue(null, privateDiagnostic);

			CopilotAccountLoginException failure =
				await Assert.ThrowsAsync<CopilotAccountLoginException>(() =>
					harness.Connector.BeginConnectAsync(
						accountId,
						expectedProviderAccountIdentity: null,
						allowAccountSwitch: false));

			Assert.Equal(
				CopilotAccountLoginFailureKind.ProcessFailed,
				failure.Kind);
			Assert.Contains(
				"重新啟動 AI Usage",
				failure.Message,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				privateDiagnostic,
				failure.ToString(),
				StringComparison.Ordinal);
			Assert.Null(failure.InnerException);
			Assert.Equal(0, harness.LoginStartCount);
			Assert.Equal(0, harness.BootstrapStartCount);
		}
		finally
		{
			latchField.SetValue(null, previousValue);
		}
	}

	[Fact]
	public async Task BeginConnectAsync_WhenContainmentLatchWinsProcessStartRace_PreservesTypedRestartFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CopilotAccountIdentity principal = CreatePrincipal(
			"NODE_CONTAINMENT_START_RACE",
			"containment-start-race-user",
			1001);
		const string restartMessage =
			"Copilot 登入程序的安全狀態無法確認。請重新啟動 AI Usage 後再試。";
		CopilotAccountLoginException expectedFailure = new(
			CopilotAccountLoginFailureKind.ProcessFailed,
			restartMessage);
		ConnectorHarness harness = new(
			temporaryDirectory.Path,
			new[]
			{
				(principal, "fixture-credential-containment-start-race")
			},
			processStarter: _ => throw expectedFailure);

		CopilotAccountLoginException failure =
			await Assert.ThrowsAsync<CopilotAccountLoginException>(() =>
				harness.Connector.BeginConnectAsync(
					accountId,
					expectedProviderAccountIdentity: null,
					allowAccountSwitch: false));

		Assert.Same(expectedFailure, failure);
		Assert.Equal(
			CopilotAccountLoginFailureKind.ProcessFailed,
			failure.Kind);
		Assert.Equal(restartMessage, failure.Message);
		Assert.Null(failure.InnerException);
		Assert.Equal(1, harness.LoginStartCount);
		Assert.Equal(0, harness.BootstrapStartCount);
	}

	[Fact]
	public void SanitizedLoginEnvironment_DoesNotInheritAmbientGitHubCredentials()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Dictionary<string, string?> ambientEnvironment = new(
			StringComparer.OrdinalIgnoreCase)
		{
			["COPILOT_GITHUB_TOKEN"] = "ambient-private-value-a",
			["GH_TOKEN"] = "ambient-private-value-b",
			["GITHUB_TOKEN"] = "ambient-private-value-c",
			["HOME"] = @"C:\ambient-home",
			["HTTPS_PROXY"] = "https://proxy.example.test:8443"
		};

		IReadOnlyDictionary<string, string> environment =
			CopilotProcessEnvironment.CreateSanitizedEnvironment(
				ambientEnvironment,
				temporaryDirectory.Path);

		Assert.False(environment.ContainsKey("COPILOT_GITHUB_TOKEN"));
		Assert.False(environment.ContainsKey("GH_TOKEN"));
		Assert.False(environment.ContainsKey("GITHUB_TOKEN"));
		Assert.Equal(
			Path.GetFullPath(temporaryDirectory.Path),
			environment["HOME"]);
		Assert.Equal(
			"https://proxy.example.test:8443",
			environment["HTTPS_PROXY"]);
	}

	[Fact]
	public void BootstrapRuntimeStartInfo_UsesOfficialContainedTcpContract()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executableDirectory = Path.Combine(
			temporaryDirectory.Path,
			"runtime");
		Directory.CreateDirectory(executableDirectory);
		string executablePath = Path.Combine(
			executableDirectory,
			"copilot.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		string homeDirectory = Path.Combine(
			temporaryDirectory.Path,
			"bootstrap-home");
		const string connectionToken = "synthetic-connection-token";

		ProcessStartInfo startInfo =
			CopilotCliAccountConnector.CreateBootstrapRuntimeStartInfo(
				executablePath,
				homeDirectory,
				connectionToken);

		Assert.Equal(
			new[]
			{
				"--headless",
				"--no-auto-update",
				"--log-level",
				"none"
			},
			startInfo.ArgumentList);
		Assert.Equal(Path.GetFullPath(executablePath), startInfo.FileName);
		Assert.Equal(Path.GetFullPath(homeDirectory), startInfo.WorkingDirectory);
		Assert.False(startInfo.UseShellExecute);
		Assert.True(startInfo.CreateNoWindow);
		Assert.False(startInfo.RedirectStandardInput);
		Assert.True(startInfo.RedirectStandardOutput);
		Assert.True(startInfo.RedirectStandardError);
		Assert.Equal(
			Path.GetFullPath(homeDirectory),
			startInfo.Environment["COPILOT_HOME"]);
		Assert.Equal(
			connectionToken,
			startInfo.Environment["COPILOT_CONNECTION_TOKEN"]);
		Assert.DoesNotContain(connectionToken, startInfo.ArgumentList);
		Assert.DoesNotContain("--stdio", startInfo.ArgumentList);
		Assert.DoesNotContain("--port", startInfo.ArgumentList);
		Assert.DoesNotContain("--no-auto-login", startInfo.ArgumentList);
		Assert.False(startInfo.Environment.ContainsKey("GH_TOKEN"));
		Assert.False(startInfo.Environment.ContainsKey("GITHUB_TOKEN"));
	}

	[Theory]
	[InlineData("CLI server listening on port 1", 1)]
	[InlineData("CLI server listening on port 65535", 65535)]
	[InlineData("CLI server listening on port 43123", 43123)]
	public void BootstrapRuntimePortAnnouncement_WithOfficialFormat_IsAccepted(
		string announcement,
		int expectedPort)
	{
		bool success =
			CopilotCliAccountConnector.TryParseBootstrapRuntimePortAnnouncement(
				announcement,
				out int port);

		Assert.True(success);
		Assert.Equal(expectedPort, port);
	}

	[Theory]
	[InlineData("")]
	[InlineData("listening on port 43123")]
	[InlineData(" CLI server listening on port 43123")]
	[InlineData("runtime CLI server listening on port 43123")]
	[InlineData("cli server listening on port 43123")]
	[InlineData("CLI server listening on port 0")]
	[InlineData("CLI server listening on port 65536")]
	[InlineData("CLI server listening on port +43123")]
	[InlineData("CLI server listening on port 43123 ")]
	[InlineData("CLI server listening on port 43123 extra")]
	public void BootstrapRuntimePortAnnouncement_WithUntrustedFormat_IsRejected(
		string announcement)
	{
		bool success =
			CopilotCliAccountConnector.TryParseBootstrapRuntimePortAnnouncement(
				announcement,
				out int port);

		Assert.False(success);
		Assert.Equal(0, port);
	}

	[Fact]
	public async Task BootstrapRuntimePortReader_WhenRuntimeExitsEarly_DoesNotEchoOutput()
	{
		const string privateOutput = "synthetic-private-runtime-output";
		await using MemoryStream output = new(
			Encoding.UTF8.GetBytes(privateOutput + "\r\n"));

		InvalidDataException failure =
			await Assert.ThrowsAsync<InvalidDataException>(() =>
				CopilotCliAccountConnector.ReadBootstrapRuntimePortAsync(
					output,
					CancellationToken.None));

		Assert.DoesNotContain(
			privateOutput,
			failure.ToString(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task BootstrapRuntimePortReader_WhenOutputExceedsLimit_FailsBoundedly()
	{
		byte[] oversizedOutput = Enumerable.Repeat(
			(byte)'x',
			(64 * 1024) + 1).ToArray();
		await using MemoryStream output = new(oversizedOutput);

		InvalidDataException failure =
			await Assert.ThrowsAsync<InvalidDataException>(() =>
				CopilotCliAccountConnector.ReadBootstrapRuntimePortAsync(
					output,
					CancellationToken.None));

		Assert.Contains("安全上限", failure.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(
			new string('x', 256),
			failure.ToString(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task BundledBootstrapRuntime_ConnectsThroughContainedLoopbackTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath =
			CopilotCliAccountConnector.ResolveExecutablePath(
				AppContext.BaseDirectory,
				System.Runtime.InteropServices.RuntimeInformation
					.ProcessArchitecture,
				File.Exists) ??
			throw new FileNotFoundException(
				"The bundled Copilot runtime was not staged for the test.");
		string homeDirectory = Path.Combine(
			temporaryDirectory.Path,
			"bootstrap-home");
		string connectionToken = $"synthetic-{Guid.NewGuid():N}";
		ProcessStartInfo startInfo =
			CopilotCliAccountConnector.CreateBootstrapRuntimeStartInfo(
				executablePath,
				homeDirectory,
				connectionToken);
		WindowsJobContainedProcess? runtimeProcess = null;
		CopilotClient? client = null;
		Task standardErrorDrainTask = Task.CompletedTask;
		Task standardOutputDrainTask = Task.CompletedTask;
		bool isTreeEmptyConfirmed = false;

		try
		{
			runtimeProcess = WindowsJobContainedProcess.Start(
				startInfo,
				static () => { });
			standardErrorDrainTask = runtimeProcess.StandardError.CopyToAsync(
				Stream.Null);
			using CancellationTokenSource startupSource = new(
				TimeSpan.FromSeconds(30));
			int port = await CopilotCliAccountConnector
				.ReadBootstrapRuntimePortAsync(
					runtimeProcess.StandardOutput,
					startupSource.Token);
			standardOutputDrainTask = runtimeProcess.StandardOutput.CopyToAsync(
				Stream.Null);
			client = new CopilotClient(new CopilotClientOptions
			{
				Connection = RuntimeConnection.ForUri(
					$"http://127.0.0.1:{port}",
					connectionToken),
				LogLevel = CopilotLogLevel.None
			});

			await client.StartAsync(startupSource.Token);
			Assert.Equal(port, client.RuntimePort);
			await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
			await runtimeProcess.TerminateTreeAndConfirmEmptyAsync(
				TimeSpan.FromSeconds(10));
			isTreeEmptyConfirmed = true;
			await Task.WhenAll(
				standardOutputDrainTask,
				standardErrorDrainTask).WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			if (client is not null)
			{
				try
				{
					await client.ForceStopAsync();
				}
				catch
				{
				}

				try
				{
					await client.DisposeAsync();
				}
				catch
				{
				}
			}

			if (runtimeProcess is not null)
			{
				if (!isTreeEmptyConfirmed)
				{
					try
					{
						await runtimeProcess.TerminateTreeAndConfirmEmptyAsync(
							TimeSpan.FromSeconds(10));
					}
					catch
					{
					}
				}

				runtimeProcess.Dispose();
			}
		}
	}

	private static CopilotAccountIdentity CreatePrincipal(
		string nodeId,
		string login,
		long databaseId)
	{
		return new CopilotAccountIdentity(
			"github.com",
			nodeId,
			databaseId,
			login);
	}

	private static string GetHomeDirectory(string testRoot, Guid accountId)
	{
		return Path.GetFullPath(Path.Combine(
			testRoot,
			"copilot",
			accountId.ToString("N"),
			"home"));
	}

	private static string GetBootstrapRoot(string testRoot, Guid accountId)
	{
		return Path.Combine(
			Path.GetDirectoryName(GetHomeDirectory(testRoot, accountId))!,
			"bootstrap");
	}

	private static string GetInternetCachePath(
		string bootstrapAttempt,
		string leafName)
	{
		return Path.Combine(
			bootstrapAttempt,
			"AppData",
			"Local",
			"Microsoft",
			"Windows",
			"INetCache",
			leafName);
	}

	private static async Task WaitUntilAsync(
		Func<bool> condition,
		TimeSpan timeout)
	{
		ArgumentNullException.ThrowIfNull(condition);
		DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
		while (!condition())
		{
			if (DateTimeOffset.UtcNow >= deadline)
			{
				throw new TimeoutException(
					"Synthetic test condition was not reached in time.");
			}

			await Task.Delay(TimeSpan.FromMilliseconds(20));
		}
	}
}
