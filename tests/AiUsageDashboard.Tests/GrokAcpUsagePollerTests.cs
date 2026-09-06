using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class GrokAcpUsagePollerTests
{
	private sealed class FakeClient : IGrokAcpUsageClient
	{
		private readonly Func<
			GrokAcpLaunchOptions,
			Func<GrokPrincipal, bool>?,
			Task<GrokUsagePollResult>> _query;

		internal GrokAcpLaunchOptions? ObservedOptions { get; private set; }

		internal Func<GrokPrincipal, bool>? ObservedPrincipalMatcher
		{
			get;
			private set;
		}

		internal FakeClient(
			Func<GrokAcpLaunchOptions, Task<GrokUsagePollResult>> query)
			: this((options, _) => query(options))
		{
		}

		internal FakeClient(
			Func<
				GrokAcpLaunchOptions,
				Func<GrokPrincipal, bool>?,
				Task<GrokUsagePollResult>> query)
		{
			_query = query;
		}

		public Task<GrokUsagePollResult> QueryAsync(
			GrokAcpLaunchOptions options,
			CancellationToken cancellationToken,
			ProviderProcessOperationTracker? operationTracker = null,
			Func<GrokPrincipal, bool>? principalMatcher = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ObservedOptions = options;
			ObservedPrincipalMatcher = principalMatcher;
			return _query(options, principalMatcher);
		}
	}

	private sealed class StubValidator : IGrokCliExecutableValidator
	{
		private readonly GrokValidatedExecutable _executable;

		internal int ResolveCount { get; private set; }

		internal StubValidator(GrokValidatedExecutable executable)
		{
			_executable = executable;
		}

		public Task<GrokValidatedExecutable> ResolveAndValidateAsync(
			CancellationToken cancellationToken = default,
			ProviderProcessOperationTracker? operationTracker = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ResolveCount++;
			return Task.FromResult(_executable);
		}

		public Task<GrokValidatedExecutable> ValidateAsync(
			string executablePath,
			CancellationToken cancellationToken = default,
			ProviderProcessOperationTracker? operationTracker = null)
		{
			throw new InvalidOperationException("Direct validation is not used.");
		}
	}

	[Fact]
	public async Task PollAsync_WithUnverifiedVersion_PreservesEvidenceOnSuccessfulResult()
	{
		Guid accountId = Guid.NewGuid();
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		CliVersionEvidence evidence = CliVersionPolicies.AssessGrok(
			new GrokExecutableVersion(1, 0, 4));
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		FakeClient client = new(_ =>
		{
			using WindowsOfficialCliExecutableLease activeLease =
				executableLease.Duplicate();
			return Task.FromResult(new GrokUsagePollResult(
				new GrokPrincipal("consumer", "principal"),
				new GrokWeeklyUsage(
					25,
					DateTimeOffset.UtcNow - TimeSpan.FromDays(7),
					DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
				DateTimeOffset.UtcNow));
		});
		(string Home, string Working)? prepared = null;
		GrokAcpUsagePoller poller = CreatePoller(
			accountId,
			client,
			new GrokValidatedExecutable(
				executablePath,
				evidence,
				executableLease),
			homeDirectory,
			workingDirectory,
			(home, working) => prepared = (home, working));

		GrokUsagePollResult result = await poller.PollAsync(accountId);

		Assert.Same(evidence, result.VersionEvidence);
		Assert.Equal((homeDirectory, workingDirectory), prepared);
		Assert.NotNull(client.ObservedOptions);
		Assert.Equal(executablePath, client.ObservedOptions.ExecutablePath);
		Assert.Equal(homeDirectory, client.ObservedOptions.HomeDirectory);
		Assert.Equal(workingDirectory, client.ObservedOptions.WorkingDirectory);
		Assert.Throws<ObjectDisposedException>(() => executableLease.Duplicate());
	}

	[Fact]
	public async Task PollBoundAsync_WithValidBinding_PassesSaltedPrincipalMatcher()
	{
		Guid accountId = Guid.NewGuid();
		GrokAccountBinding binding = GrokAccountBinding.Create(
			accountId,
			"consumer",
			"bound-principal");
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		CliVersionEvidence evidence = CliVersionPolicies.AssessGrok(
			new GrokExecutableVersion(1, 0, 4));
		FakeClient client = new((_, principalMatcher) =>
		{
			Func<GrokPrincipal, bool> matcher =
				Assert.IsType<Func<GrokPrincipal, bool>>(principalMatcher);
			Assert.True(matcher(new GrokPrincipal(
				"consumer",
				"bound-principal")));
			Assert.False(matcher(new GrokPrincipal(
				"consumer",
				"different-principal")));
			return Task.FromResult(CreateSuccessfulResult(
				"bound-principal"));
		});
		GrokAcpUsagePoller poller = CreatePoller(
			accountId,
			client,
			new GrokValidatedExecutable(executablePath, evidence),
			homeDirectory,
			workingDirectory,
			(_, _) => { });

		GrokUsagePollResult result = await poller.PollBoundAsync(
			accountId,
			binding);

		Assert.Equal("bound-principal", result.Principal.PrincipalId);
		Assert.Same(evidence, result.VersionEvidence);
		Assert.NotNull(client.ObservedPrincipalMatcher);
	}

	[Theory]
	[InlineData("type")]
	[InlineData("id")]
	public async Task PollBoundAsync_WhenObservedPrincipalIsInvalid_ThrowsSchemaFailure(
		string invalidPart)
	{
		Guid accountId = Guid.NewGuid();
		GrokAccountBinding binding = GrokAccountBinding.Create(
			accountId,
			"consumer",
			"bound-principal");
		GrokPrincipal invalidPrincipal = invalidPart switch
		{
			"type" => new GrokPrincipal(
				new string('t', 65),
				"bound-principal"),
			"id" => new GrokPrincipal(
				"consumer",
				new string('i', 1025)),
			_ => throw new ArgumentOutOfRangeException(nameof(invalidPart))
		};
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		CliVersionEvidence evidence = CliVersionPolicies.AssessGrok(
			new GrokExecutableVersion(1, 0, 4));
		FakeClient client = new((_, principalMatcher) =>
		{
			Func<GrokPrincipal, bool> matcher =
				Assert.IsType<Func<GrokPrincipal, bool>>(principalMatcher);
			if (!matcher(invalidPrincipal))
			{
				return Task.FromException<GrokUsagePollResult>(
					new GrokPrincipalMismatchException());
			}

			return Task.FromResult(CreateSuccessfulResult("bound-principal"));
		});
		GrokAcpUsagePoller poller = CreatePoller(
			accountId,
			client,
			new GrokValidatedExecutable(executablePath, evidence),
			homeDirectory,
			workingDirectory,
			(_, _) => { });

		await Assert.ThrowsAsync<GrokUsageSchemaException>(() =>
			poller.PollBoundAsync(accountId, binding));
	}

	[Theory]
	[InlineData("account")]
	[InlineData("public")]
	[InlineData("salt")]
	[InlineData("fingerprint")]
	public async Task PollBoundAsync_WithInvalidBinding_FailsBeforePreparation(
		string invalidPart)
	{
		Guid accountId = Guid.NewGuid();
		GrokAccountBinding validBinding = GrokAccountBinding.Create(
			accountId,
			"consumer",
			"bound-principal");
		GrokAccountBinding binding = invalidPart switch
		{
			"account" => validBinding with { AccountId = Guid.NewGuid() },
			"public" => validBinding with { PublicBindingId = Guid.Empty },
			"salt" => validBinding with { SaltBase64 = "invalid" },
			"fingerprint" => validBinding with
			{
				PrincipalFingerprintSha256 = new string('a', 64)
			},
			_ => throw new ArgumentOutOfRangeException(nameof(invalidPart))
		};
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		StubValidator validator = new(new GrokValidatedExecutable(
			executablePath,
			CliVersionPolicies.AssessGrok(
				new GrokExecutableVersion(1, 0, 4))));
		bool preparedDirectories = false;
		FakeClient client = new(_ => throw new InvalidOperationException(
			"Invalid binding must fail before QueryAsync."));
		GrokAcpUsagePoller poller = new(
			client,
			validator,
			new GrokAccountOperationGate(),
			_ => homeDirectory,
			_ => workingDirectory,
			(_, _) => preparedDirectories = true,
			new GrokProcessContainmentState());

		await Assert.ThrowsAsync<GrokPrincipalBindingInvalidException>(() =>
			poller.PollBoundAsync(accountId, binding));

		Assert.Equal(0, validator.ResolveCount);
		Assert.False(preparedDirectories);
		Assert.Null(client.ObservedOptions);
	}

	[Fact]
	public void UsagePollResult_WithContradictoryState_RejectsConstruction()
	{
		GrokPrincipal principal = new("consumer", "principal");

		Assert.Throws<ArgumentException>(() => new GrokUsagePollResult(
			principal,
			weeklyUsage: null,
			DateTimeOffset.UtcNow));
		Assert.Throws<ArgumentException>(() => new GrokUsagePollResult(
			principal,
			new GrokWeeklyUsage(
				25,
				DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow.AddDays(7)),
			DateTimeOffset.UtcNow,
			usageAvailability: GrokUsageAvailability.AuthenticationRequired,
			currentAuthUsability: GrokCurrentAuthUsability.Unusable));
	}

	[Fact]
	public void UsagePollResult_WithTransientUnknownAuth_DoesNotPermitBindingCommit()
	{
		GrokUsagePollResult result = new(
			new GrokPrincipal("consumer", "principal"),
			weeklyUsage: null,
			DateTimeOffset.UtcNow,
			usageAvailability: GrokUsageAvailability.TransientProbeError,
			currentAuthUsability: GrokCurrentAuthUsability.Unknown);

		Assert.True(result.ScopeObserved);
		Assert.False(result.UsageAvailable);
		Assert.False(result.CurrentAuthUsable);
		Assert.False(result.CanCommitBinding);
	}

	[Fact]
	public async Task PollAsync_WhenQueryFails_AttachesEvidenceWithoutChangingExceptionType()
	{
		Guid accountId = Guid.NewGuid();
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		CliVersionEvidence evidence = CliVersionPolicies.AssessGrok(
			new GrokExecutableVersion(1, 0, 4));
		GrokUsageSchemaException failure = new("schema changed");
		FakeClient client = new(_ =>
			Task.FromException<GrokUsagePollResult>(failure));
		GrokAcpUsagePoller poller = CreatePoller(
			accountId,
			client,
			new GrokValidatedExecutable(executablePath, evidence),
			homeDirectory,
			workingDirectory,
			(_, _) => { });

		GrokUsageSchemaException exception =
			await Assert.ThrowsAsync<GrokUsageSchemaException>(
				() => poller.PollAsync(accountId));

		Assert.Same(failure, exception);
		string message = exception.AppendVersionDiagnostic("原始錯誤。");
		Assert.Contains("偵測到 Grok Build CLI 1.0.4", message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PollAsync_WhenQueryHasLocalIoFailure_DoesNotAttachEvidence()
	{
		Guid accountId = Guid.NewGuid();
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		CliVersionEvidence evidence = CliVersionPolicies.AssessGrok(
			new GrokExecutableVersion(1, 0, 4));
		IOException failure = new("local stream failure");
		FakeClient client = new(_ =>
			Task.FromException<GrokUsagePollResult>(failure));
		GrokAcpUsagePoller poller = CreatePoller(
			accountId,
			client,
			new GrokValidatedExecutable(executablePath, evidence),
			homeDirectory,
			workingDirectory,
			(_, _) => { });

		IOException exception = await Assert.ThrowsAsync<IOException>(
			() => poller.PollAsync(accountId));

		Assert.Equal(
			"原始錯誤。",
			exception.AppendVersionDiagnostic("原始錯誤。"));
	}

	[Fact]
	public async Task PollAsync_WhenContainmentIsAlreadyCompromised_RejectsBeforeValidation()
	{
		Guid accountId = Guid.NewGuid();
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		GrokProcessContainmentState containmentState = new();
		containmentState.MarkCompromised();
		StubValidator validator = new(new GrokValidatedExecutable(
			executablePath,
			CliVersionPolicies.AssessGrok(
				new GrokExecutableVersion(1, 0, 4))));
		bool preparedDirectories = false;
		FakeClient client = new(_ => throw new InvalidOperationException(
			"Query must not run after containment is compromised."));
		GrokAcpUsagePoller poller = new(
			client,
			validator,
			new GrokAccountOperationGate(),
			_ => homeDirectory,
			_ => workingDirectory,
			(_, _) => preparedDirectories = true,
			containmentState);

		await Assert.ThrowsAsync<GrokProcessContainmentException>(
			() => poller.PollAsync(accountId));

		Assert.Equal(0, validator.ResolveCount);
		Assert.False(preparedDirectories);
		Assert.Null(client.ObservedOptions);
	}

	[Fact]
	public async Task PollAsync_WhenContainmentChangesDuringPreparation_RejectsBeforeValidation()
	{
		Guid accountId = Guid.NewGuid();
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		GrokProcessContainmentState containmentState = new();
		StubValidator validator = new(new GrokValidatedExecutable(
			executablePath,
			CliVersionPolicies.AssessGrok(
				new GrokExecutableVersion(1, 0, 4))));
		FakeClient client = new(_ => throw new InvalidOperationException(
			"Query must not run after containment is compromised."));
		GrokAcpUsagePoller poller = new(
			client,
			validator,
			new GrokAccountOperationGate(),
			_ => homeDirectory,
			_ => workingDirectory,
			(_, _) => containmentState.MarkCompromised(),
			containmentState);

		await Assert.ThrowsAsync<GrokProcessContainmentException>(
			() => poller.PollAsync(accountId));

		Assert.Equal(0, validator.ResolveCount);
		Assert.Null(client.ObservedOptions);
	}

	[Fact]
	public async Task PollAsync_WhenQueryReportsContainmentFailure_QuarantinesExecutableLease()
	{
		Guid accountId = Guid.NewGuid();
		(string executablePath, string homeDirectory, string workingDirectory) =
			CreatePaths();
		GrokProcessContainmentState containmentState = new();
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		GrokProcessContainmentException failure = new(
			"typed containment failure");
		FakeClient client = new(_ =>
		{
			containmentState.MarkCompromised();
			return Task.FromException<GrokUsagePollResult>(failure);
		});
		GrokAcpUsagePoller poller = CreatePoller(
			accountId,
			client,
			new GrokValidatedExecutable(
				executablePath,
				CliVersionPolicies.AssessGrok(
					new GrokExecutableVersion(1, 0, 4)),
				executableLease),
			homeDirectory,
			workingDirectory,
			(_, _) => { },
			containmentState);

		GrokProcessContainmentException exception =
			await Assert.ThrowsAsync<GrokProcessContainmentException>(
				() => poller.PollAsync(accountId));

		Assert.Same(failure, exception);
		Assert.Equal(1, containmentState.QuarantinedExecutableLeaseCount);
		using WindowsOfficialCliExecutableLease quarantinedLease =
			executableLease.Duplicate();
	}

	private static GrokAcpUsagePoller CreatePoller(
		Guid accountId,
		IGrokAcpUsageClient client,
		GrokValidatedExecutable executable,
		string homeDirectory,
		string workingDirectory,
		Action<string, string> prepareDirectories,
		GrokProcessContainmentState? containmentState = null)
	{
		return new GrokAcpUsagePoller(
			client,
			new StubValidator(executable),
			new GrokAccountOperationGate(),
			id => id == accountId
				? homeDirectory
				: throw new InvalidOperationException(),
			id => id == accountId
				? workingDirectory
				: throw new InvalidOperationException(),
			prepareDirectories,
			containmentState ?? new GrokProcessContainmentState());
	}

	private static (string ExecutablePath, string HomeDirectory, string WorkingDirectory)
		CreatePaths()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			Guid.NewGuid().ToString("N"));
		return (
			Path.Combine(root, "bin", "grok.exe"),
			Path.Combine(root, "account", "home"),
			Path.Combine(root, "account", "blank-workspace"));
	}

	private static GrokUsagePollResult CreateSuccessfulResult(
		string principalId)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new GrokUsagePollResult(
			new GrokPrincipal("consumer", principalId),
			new GrokWeeklyUsage(
				25,
				now - TimeSpan.FromDays(2),
				now + TimeSpan.FromDays(5)),
			now);
	}
}
