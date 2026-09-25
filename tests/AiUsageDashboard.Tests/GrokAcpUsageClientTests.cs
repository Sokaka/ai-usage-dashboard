using System.Diagnostics;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Providers;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class GrokAcpUsageClientTests
{
	private sealed class CancellationAwarePendingReadStream : Stream
	{
		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			return WaitForCancellationAsync(cancellationToken);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		private static async ValueTask<int> WaitForCancellationAsync(
			CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 0;
		}
	}

	private sealed class FakeProcess : IGrokAcpProcess
	{
		private readonly TaskCompletionSource _disposeCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly Stream _standardError;
		private readonly MemoryStream _standardInput = new();
		private readonly Stream _standardOutput;

		internal int DisposeCallCount { get; private set; }

		internal int TerminateCallCount { get; private set; }

		internal int WaitForExitCallCount { get; private set; }

		internal Task DisposeCompletion => _disposeCompletion.Task;

		public Stream StandardError => _standardError;

		public Stream StandardInput => _standardInput;

		public Stream StandardOutput => _standardOutput;

		internal FakeProcess(string standardOutput, string standardError = "")
			: this(
				new MemoryStream(Encoding.UTF8.GetBytes(standardOutput)),
				new MemoryStream(Encoding.UTF8.GetBytes(standardError)))
		{
		}

		internal FakeProcess(Stream standardOutput, Stream standardError)
		{
			_standardOutput = standardOutput ??
				throw new ArgumentNullException(nameof(standardOutput));
			_standardError = standardError ??
				throw new ArgumentNullException(nameof(standardError));
		}

		public ValueTask DisposeAsync()
		{
			DisposeCallCount++;
			_standardInput.Dispose();
			_standardOutput.Dispose();
			_standardError.Dispose();
			_disposeCompletion.TrySetResult();
			return ValueTask.CompletedTask;
		}

		public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			WaitForExitCallCount++;
			return Task.FromResult(0);
		}

		public Task TerminateTreeAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			TerminateCallCount++;
			return Task.CompletedTask;
		}

		internal string ReadStandardInput()
		{
			return Encoding.UTF8.GetString(_standardInput.ToArray());
		}
	}

	private sealed class FakeProcessFactory : IGrokAcpProcessFactory
	{
		private readonly FakeProcess _process;

		internal GrokAcpLaunchOptions? ObservedOptions { get; private set; }

		internal int StartCallCount { get; private set; }

		internal FakeProcessFactory(FakeProcess process)
		{
			_process = process;
		}

		public Task<IGrokAcpProcess> StartAsync(
			GrokAcpLaunchOptions options,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			StartCallCount++;
			ObservedOptions = options;
			return Task.FromResult<IGrokAcpProcess>(_process);
		}
	}

	private sealed class BlockingProcessFactory : IGrokAcpProcessFactory
	{
		private readonly FakeProcess _process;
		private readonly ManualResetEventSlim _releaseStart;

		internal TaskCompletionSource StartEntered { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal BlockingProcessFactory(
			FakeProcess process,
			ManualResetEventSlim releaseStart)
		{
			_process = process;
			_releaseStart = releaseStart;
		}

		public Task<IGrokAcpProcess> StartAsync(
			GrokAcpLaunchOptions options,
			CancellationToken cancellationToken)
		{
			StartEntered.TrySetResult();
			_releaseStart.Wait();
			return Task.FromResult<IGrokAcpProcess>(_process);
		}
	}

	private sealed class FakeLease : IDisposable
	{
		private int _isDisposed;

		internal TaskCompletionSource DisposeCompletion { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
			{
				DisposeCompletion.TrySetResult();
			}
		}
	}

	private sealed class FakeTimeProvider : TimeProvider
	{
		private readonly DateTimeOffset _utcNow;

		internal FakeTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow()
		{
			return _utcNow;
		}
	}

	[Fact]
	public async Task QueryAsync_WithOfficialSequence_ReturnsWeeklyUsageAndSendsOnlyReadOnlyRequests()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		GrokAcpLaunchOptions options = CreateLaunchOptions();
		FakeProcess process = new(CreateSuccessfulOutput(
			now,
			creditUsagePercent: 27.5,
			accountEmail: "person@example.com",
			subscriptionTier: "SuperGrok"));
		FakeProcessFactory factory = new(process);
		GrokAcpUsageClient client = new(factory, new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			options,
			CancellationToken.None);

		Assert.Equal(
			new GrokPrincipal(
				"consumer",
				"opaque-principal",
				"person@example.com"),
			result.Principal);
		GrokWeeklyUsage weeklyUsage = Assert.IsType<GrokWeeklyUsage>(
			result.WeeklyUsage);
		Assert.Equal(27.5, weeklyUsage.UsedPercent);
		Assert.Equal(now - TimeSpan.FromDays(3), weeklyUsage.PeriodStartsAt);
		Assert.Equal(now + TimeSpan.FromDays(4), weeklyUsage.ResetsAt);
		Assert.Equal(now, result.ObservedAt);
		Assert.Equal(GrokUsageAvailability.Available, result.UsageAvailability);
		Assert.Equal(GrokCurrentAuthUsability.Usable, result.CurrentAuthUsability);
		Assert.Equal("SuperGrok", result.PlanTier);
		Assert.True(result.ScopeObserved);
		Assert.True(result.UsageAvailable);
		Assert.True(result.CurrentAuthUsable);
		Assert.True(result.CanCommitBinding);
		Assert.Equal(1, factory.StartCallCount);
		Assert.Equal(options, factory.ObservedOptions);
		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(0, process.WaitForExitCallCount);
		Assert.Equal(1, process.DisposeCallCount);

		AssertRequestSequence(process.ReadStandardInput());
	}

	[Fact]
	public async Task QueryAsync_WithMalformedSubscriptionTier_KeepsUsageAndHidesPlan()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(CreateSuccessfulOutput(
			now,
			creditUsagePercent: 27.5,
			subscriptionTier: 42));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(
			27.5,
			Assert.IsType<GrokWeeklyUsage>(result.WeeklyUsage).UsedPercent);
		Assert.Null(result.PlanTier);
		Assert.Equal(GrokUsageAvailability.Available, result.UsageAvailability);
	}

	[Theory]
	[InlineData("{\"subscriptionTier\":\"SuperGrok\"}", "SuperGrok")]
	[InlineData("{\"subscription_tier\":\"SuperGrok\"}", "SuperGrok")]
	[InlineData("{\"subscriptionTier\":\"SuperGrok\",\"subscription_tier\":\"SuperGrok\"}", "SuperGrok")]
	[InlineData("{\"subscriptionTier\":\"SuperGrok\",\"subscription_tier\":\"Other\"}", null)]
	[InlineData("{\"subscriptionTier\":42,\"subscription_tier\":\"SuperGrok\"}", null)]
	[InlineData("{}", null)]
	public void ParsePlanTier_WithCurrentAndLegacyFields_UsesOnlyConsistentSafeValues(
		string billingJson,
		string? expected)
	{
		using JsonDocument billing = JsonDocument.Parse(billingJson);

		Assert.Equal(expected, GrokAcpUsageClient.ParsePlanTier(billing.RootElement));
	}

	[Fact]
	public async Task QueryAsync_WhenProcessStartBlocks_TimesOutWithoutHoldingCallerAndCleansLateProcess()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(CreateSuccessfulOutput(now, creditUsagePercent: 27.5));
		using ManualResetEventSlim releaseStart = new(initialState: false);
		BlockingProcessFactory factory = new(process, releaseStart);
		ProviderProcessOperationTracker operationTracker = new();
		FakeLease rawOperationLease = new();
		IDisposable operationLease =
			operationTracker.HoldLease(rawOperationLease);
		GrokAcpUsageClient client = new(
			factory,
			operationTimeout: TimeSpan.FromMilliseconds(100),
			cleanupTimeout: TimeSpan.FromSeconds(1));
		using CancellationTokenSource fallbackRelease = new(TimeSpan.FromSeconds(2));
		using CancellationTokenRegistration fallbackRegistration =
			fallbackRelease.Token.Register(releaseStart.Set);
		Stopwatch invocationStopwatch = Stopwatch.StartNew();

		Task<GrokUsagePollResult> queryTask = client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None,
			operationTracker);

		invocationStopwatch.Stop();
		Assert.True(
			invocationStopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
			$"QueryAsync synchronously held its caller for {invocationStopwatch.Elapsed}.");
		await factory.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
		GrokAcpFailureException exception =
			await Assert.ThrowsAsync<GrokAcpFailureException>(() => queryTask)
				.WaitAsync(TimeSpan.FromSeconds(1));
		Assert.IsType<TimeoutException>(exception.InnerException);
		operationLease.Dispose();
		Assert.Equal(0, process.DisposeCallCount);
		Assert.False(rawOperationLease.DisposeCompletion.Task.IsCompleted);

		releaseStart.Set();
		await process.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(1));
		await rawOperationLease.DisposeCompletion.Task.WaitAsync(
			TimeSpan.FromSeconds(1));

		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(1, process.DisposeCallCount);
	}

	[Fact]
	public async Task QueryAsync_WhenStderrOverflowsWhileStdoutWaits_FailsPromptlyAndTerminatesTree()
	{
		FakeProcess process = new(
			new CancellationAwarePendingReadStream(),
			new MemoryStream(new byte[(64 * 1024) + 1], writable: false));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			operationTimeout: TimeSpan.FromSeconds(10),
			cleanupTimeout: TimeSpan.FromSeconds(1));
		Stopwatch stopwatch = Stopwatch.StartNew();

		GrokUsageSchemaException exception =
			await Assert.ThrowsAsync<GrokUsageSchemaException>(() =>
				client.QueryAsync(CreateLaunchOptions(), CancellationToken.None));

		stopwatch.Stop();
		Assert.Contains("stderr", exception.Message, StringComparison.Ordinal);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(5),
			$"stderr overflow took {stopwatch.Elapsed} to surface.");
		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(1, process.DisposeCallCount);
	}

	[Fact]
	public async Task QueryAsync_WhenWeeklyPercentIsAbsent_ReturnsUnknownPercent()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(CreateSuccessfulOutput(now, creditUsagePercent: null));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Null(Assert.IsType<GrokWeeklyUsage>(result.WeeklyUsage).UsedPercent);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WhenAccountIsNotUnifiedBilling_ReportsUnsupportedUsableScope()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(
			CreateSuccessfulOutput(
				now,
				creditUsagePercent: 25,
				isUnifiedBillingUser: false));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(GrokUsageAvailability.Unsupported, result.UsageAvailability);
		Assert.Equal(GrokCurrentAuthUsability.Usable, result.CurrentAuthUsability);
		Assert.True(result.ScopeObserved);
		Assert.False(result.UsageAvailable);
		Assert.True(result.CurrentAuthUsable);
		Assert.True(result.CanCommitBinding);
		Assert.Null(result.WeeklyUsage);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WhenBillingPeriodIsNotWeekly_ReportsUnsupportedUsableScope()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(
			CreateSuccessfulOutput(
				now,
				creditUsagePercent: 25,
				periodType: "USAGE_PERIOD_TYPE_MONTHLY"));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(GrokUsageAvailability.Unsupported, result.UsageAvailability);
		Assert.Equal(GrokCurrentAuthUsability.Usable, result.CurrentAuthUsability);
		Assert.Null(result.WeeklyUsage);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WhenBillingPeriodTypeIsMalformed_ReportsTransientProbeError()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(
			CreateSuccessfulOutput(
				now,
				creditUsagePercent: 25,
				periodType: null));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(
			GrokUsageAvailability.TransientProbeError,
			result.UsageAvailability);
		Assert.Equal(GrokCurrentAuthUsability.Usable, result.CurrentAuthUsability);
		Assert.True(result.CanCommitBinding);
		Assert.Null(result.WeeklyUsage);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithBothLegacyNamespaces_FallsBackIndependently()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(
			CreateSuccessfulOutput(
				now,
				creditUsagePercent: 42.5,
				useLegacyAuthNamespace: true,
				useLegacyBillingNamespace: true,
				accountEmail: "legacy@example.com"));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(
			42.5,
			Assert.IsType<GrokWeeklyUsage>(result.WeeklyUsage).UsedPercent);
		Assert.Equal("legacy@example.com", result.Principal.Email);
		Assert.Null(result.PlanTier);
		Assert.Equal(
			new[]
			{
				"initialize",
				"x.ai/auth/info",
				"_x.ai/auth/info",
				"x.ai/billing",
				"_x.ai/billing"
			},
			ReadRequestMethods(process.ReadStandardInput()));
	}

	[Fact]
	public async Task QueryAsync_WithLegacyAuthAndCurrentBilling_FallsBackOnlyAuth()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(CreateSuccessfulOutput(
			now,
			creditUsagePercent: 31,
			useLegacyAuthNamespace: true));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.True(result.UsageAvailable);
		Assert.Equal(
			new[]
			{
				"initialize",
				"x.ai/auth/info",
				"_x.ai/auth/info",
				"x.ai/billing"
			},
			ReadRequestMethods(process.ReadStandardInput()));
	}

	[Fact]
	public async Task QueryAsync_WithCurrentAuthAndLegacyBilling_FallsBackOnlyBilling()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		FakeProcess process = new(CreateSuccessfulOutput(
			now,
			creditUsagePercent: 31,
			useLegacyBillingNamespace: true,
			subscriptionTier: "SuperGrok"));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.True(result.UsageAvailable);
		Assert.Equal("SuperGrok", result.PlanTier);
		Assert.Equal(
			new[]
			{
				"initialize",
				"x.ai/auth/info",
				"x.ai/billing",
				"_x.ai/billing"
			},
			ReadRequestMethods(process.ReadStandardInput()));
	}

	[Fact]
	public async Task QueryAsync_WhenPrincipalIsMissing_StopsBeforeBilling()
	{
		FakeProcess process = new(string.Join(
			'\n',
			CreateResultResponse(1, new { protocolVersion = 1 }),
			CreateResultResponse(4, new { principalType = "consumer" })) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		await Assert.ThrowsAsync<GrokUsageSchemaException>(() =>
			client.QueryAsync(CreateLaunchOptions(), CancellationToken.None));

		Assert.Equal(
			new[] { "initialize", "x.ai/auth/info" },
			ReadRequestMethods(process.ReadStandardInput()));
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Theory]
	[InlineData(64, true)]
	[InlineData(65, false)]
	public void ParsePrincipal_WithPrincipalTypeBoundary_UsesCanonicalBindingDomain(
		int principalTypeLength,
		bool shouldBeAccepted)
	{
		string principalType = new('t', principalTypeLength);
		using JsonDocument document = JsonDocument.Parse(
			JsonSerializer.Serialize(new
			{
				principalType,
				principalId = "opaque-principal"
			}));

		if (!shouldBeAccepted)
		{
			Assert.Throws<GrokUsageSchemaException>(() =>
				GrokAcpUsageClient.ParsePrincipal(document.RootElement));
			return;
		}

		GrokPrincipal principal = GrokAcpUsageClient.ParsePrincipal(
			document.RootElement);
		Assert.Equal(principalType, principal.PrincipalType);
	}

	[Theory]
	[InlineData(1024, true)]
	[InlineData(1025, false)]
	public void ParsePrincipal_WithPrincipalIdBoundary_UsesCanonicalBindingDomain(
		int principalIdLength,
		bool shouldBeAccepted)
	{
		string principalId = new('i', principalIdLength);
		using JsonDocument document = JsonDocument.Parse(
			JsonSerializer.Serialize(new
			{
				principalType = "consumer",
				principalId
			}));

		if (!shouldBeAccepted)
		{
			Assert.Throws<GrokUsageSchemaException>(() =>
				GrokAcpUsageClient.ParsePrincipal(document.RootElement));
			return;
		}

		GrokPrincipal principal = GrokAcpUsageClient.ParsePrincipal(
			document.RootElement);
		Assert.Equal(principalId, principal.PrincipalId);
	}

	[Fact]
	public async Task QueryAsync_WhenBoundPrincipalDoesNotMatch_StopsBeforeBillingWithoutLeakingPrincipal()
	{
		const string rawPrincipal = "private-principal-marker";
		FakeProcess process = new(string.Join(
			'\n',
			CreateResultResponse(1, new { protocolVersion = 1 }),
			CreatePrincipalResultResponse(4, rawPrincipal)) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		GrokPrincipalMismatchException exception =
			await Assert.ThrowsAsync<GrokPrincipalMismatchException>(() =>
				client.QueryAsync(
					CreateLaunchOptions(),
					CancellationToken.None,
					principalMatcher: _ => false));

		Assert.DoesNotContain(
			rawPrincipal,
			exception.ToString(),
			StringComparison.Ordinal);
		Assert.Equal(
			new[] { "initialize", "x.ai/auth/info" },
			ReadRequestMethods(process.ReadStandardInput()));
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithLegacyFreshHomeAuthenticationError_ReportsAuthenticationRequired()
	{
		FakeProcess process = new(string.Join(
			'\n',
			CreateResultResponse(1, new { protocolVersion = 1 }),
			CreateNotification("_x.ai/mcp/servers_updated"),
			CreateErrorResponse(4, -32601, "Method not found"),
			CreatePrincipalResultResponse(5),
			CreateErrorResponse(2, -32601, "Method not found"),
			CreateErrorResponse(
				3,
				-32000,
				"Authentication required",
				"Authentication required to fetch billing data")) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(
			GrokUsageAvailability.AuthenticationRequired,
			result.UsageAvailability);
		Assert.Equal(
			GrokCurrentAuthUsability.Unusable,
			result.CurrentAuthUsability);
		Assert.True(result.ScopeObserved);
		Assert.False(result.CurrentAuthUsable);
		Assert.False(result.CanCommitBinding);
		Assert.Null(result.WeeklyUsage);
		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(
			new[]
			{
				"initialize",
				"x.ai/auth/info",
				"_x.ai/auth/info",
				"x.ai/billing",
				"_x.ai/billing"
			},
			ReadRequestMethods(process.ReadStandardInput()));
	}

	[Fact]
	public async Task QueryAsync_WithStructuredAuthenticationError_ReportsAuthenticationRequired()
	{
		FakeProcess process = new(string.Join(
			'\n',
			CreateResultResponse(1, new { protocolVersion = 1 }),
			CreateNotification("_x.ai/mcp/servers_updated"),
			CreatePrincipalResultResponse(4),
			CreateErrorResponse(
				2,
				-32000,
				"Authentication required",
				"Authentication required to fetch billing data")) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(
			GrokUsageAvailability.AuthenticationRequired,
			result.UsageAvailability);
		Assert.Equal(
			GrokCurrentAuthUsability.Unusable,
			result.CurrentAuthUsability);
		Assert.False(result.CanCommitBinding);
		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(
			new[] { "initialize", "x.ai/auth/info", "x.ai/billing" },
			ReadRequestMethods(process.ReadStandardInput()));
	}

	[Fact]
	public async Task QueryAsync_WithGenericBillingServerError_RemainsRetryable()
	{
		FakeProcess process = new(string.Join(
			'\n',
			CreateResultResponse(1, new { protocolVersion = 1 }),
			CreatePrincipalResultResponse(4),
			CreateErrorResponse(2, -32000, "internal server error")) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		GrokAcpFailureException exception =
			await Assert.ThrowsAsync<GrokAcpFailureException>(
				() => client.QueryAsync(
					CreateLaunchOptions(),
					CancellationToken.None));

		Assert.Equal(GrokAcpFailureCategory.Unknown, exception.Category);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public void ParsePrincipal_WithInvalidOptionalEmail_KeepsPrincipalWithoutDisplayIdentity()
	{
		object?[] invalidEmails =
		[
			null,
			42,
			" ",
			new string('a', ProviderAccountIdentityRules.MaximumLength + 1),
			"line\nbreak@example.com"
		];

		foreach (object? invalidEmail in invalidEmails)
		{
			using JsonDocument document = JsonDocument.Parse(
				JsonSerializer.Serialize(new
				{
					principalType = "consumer",
					principalId = "opaque-principal",
					email = invalidEmail
				}));

			GrokPrincipal principal = GrokAcpUsageClient.ParsePrincipal(
				document.RootElement);

			Assert.Equal("consumer", principal.PrincipalType);
			Assert.Equal("opaque-principal", principal.PrincipalId);
			Assert.Null(principal.Email);
		}
	}

	[Fact]
	public async Task QueryAsync_WithBillingAuthLikeServerError_RemainsRetryable()
	{
		FakeProcess process = new(string.Join(
			'\n',
			CreateResultResponse(1, new { protocolVersion = 1 }),
			CreatePrincipalResultResponse(4),
			CreateErrorResponse(2, -32000, "Authentication required")) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		GrokAcpFailureException exception =
			await Assert.ThrowsAsync<GrokAcpFailureException>(
				() => client.QueryAsync(
					CreateLaunchOptions(),
					CancellationToken.None));

		Assert.Equal(GrokAcpFailureCategory.Unknown, exception.Category);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithTransientBillingError_DoesNotFallBackNamespace()
	{
		FakeProcess process = new(string.Join(
			'\n',
			CreateResultResponse(1, new { protocolVersion = 1 }),
			CreatePrincipalResultResponse(4),
			CreateErrorResponse(2, -32003, "temporarily unavailable")) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		GrokAcpFailureException exception =
			await Assert.ThrowsAsync<GrokAcpFailureException>(
				() => client.QueryAsync(
					CreateLaunchOptions(),
					CancellationToken.None));

		Assert.Equal(GrokAcpFailureCategory.Transient, exception.Category);
		Assert.Equal(
			new[] { "initialize", "x.ai/auth/info", "x.ai/billing" },
			ReadRequestMethods(process.ReadStandardInput()));
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithNotificationWithoutParams_SkipsNotification()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		string notification = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "future/notification"
		});
		FakeProcess process = new(
			CreateSuccessfulOutput(
				now,
				creditUsagePercent: 12.5,
				notification: notification));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(
			12.5,
			Assert.IsType<GrokWeeklyUsage>(result.WeeklyUsage).UsedPercent);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithNotificationArrayParams_SkipsNotification()
	{
		DateTimeOffset now = new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
		string notification = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "future/notification",
			@params = new object[] { "ignored" }
		});
		FakeProcess process = new(
			CreateSuccessfulOutput(
				now,
				creditUsagePercent: 12.5,
				notification: notification));
		GrokAcpUsageClient client = new(
			new FakeProcessFactory(process),
			new FakeTimeProvider(now));

		GrokUsagePollResult result = await client.QueryAsync(
			CreateLaunchOptions(),
			CancellationToken.None);

		Assert.Equal(
			12.5,
			Assert.IsType<GrokWeeklyUsage>(result.WeeklyUsage).UsedPercent);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithScalarNotificationParams_RejectsWithoutLeakingPayload()
	{
		const string sensitiveMarker = "do-not-leak-notification-payload";
		string notification = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "future/notification",
			@params = sensitiveMarker
		});
		FakeProcess process = new(notification + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		GrokUsageSchemaException exception =
			await Assert.ThrowsAsync<GrokUsageSchemaException>(
				() => client.QueryAsync(
					CreateLaunchOptions(),
					CancellationToken.None));

		Assert.DoesNotContain(sensitiveMarker, exception.ToString(), StringComparison.Ordinal);
		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithUnknownResponseId_RejectsAndTerminatesTree()
	{
		FakeProcess process = new(
			CreateResultResponse(99, new { protocolVersion = 1 }) + "\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		await Assert.ThrowsAsync<GrokUsageSchemaException>(
			() => client.QueryAsync(CreateLaunchOptions(), CancellationToken.None));

		Assert.Equal(1, process.TerminateCallCount);
		Assert.Equal(new[] { "initialize" }, ReadRequestMethods(process.ReadStandardInput()));
	}

	[Fact]
	public async Task QueryAsync_WithDuplicateResponseId_RejectsAndTerminatesTree()
	{
		FakeProcess process = new(
			"{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":1,\"result\":{}}\n");
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		await Assert.ThrowsAsync<GrokUsageSchemaException>(
			() => client.QueryAsync(CreateLaunchOptions(), CancellationToken.None));

		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task QueryAsync_WithOversizedJsonLine_RejectsAndTerminatesTree()
	{
		string oversizedOutput =
			"{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"" +
			new string('A', (128 * 1024) + 1) +
			"\"}\n";
		FakeProcess process = new(oversizedOutput);
		GrokAcpUsageClient client = new(new FakeProcessFactory(process));

		await Assert.ThrowsAsync<GrokUsageSchemaException>(
			() => client.QueryAsync(CreateLaunchOptions(), CancellationToken.None));

		Assert.Equal(1, process.TerminateCallCount);
	}

	[Fact]
	public async Task BoundedReader_WhenResponseCountExceedsLimit_RejectsNextLine()
	{
		using MemoryStream stream = new(Encoding.UTF8.GetBytes("{}\n{}\n{}\n"));
		GrokBoundedJsonLineReader reader = new(
			stream,
			maximumLineBytes: 16,
			maximumTotalBytes: 64,
			maximumResponseCount: 2);

		Assert.Equal("{}", await reader.ReadLineAsync(CancellationToken.None));
		Assert.Equal("{}", await reader.ReadLineAsync(CancellationToken.None));
		await Assert.ThrowsAsync<GrokUsageSchemaException>(
			() => reader.ReadLineAsync(CancellationToken.None));
	}

	[Fact]
	public async Task BoundedReader_WhenTotalBytesExceedLimit_RejectsInput()
	{
		using MemoryStream stream = new(Encoding.UTF8.GetBytes("1234567\n1234567\n1\n"));
		GrokBoundedJsonLineReader reader = new(
			stream,
			maximumLineBytes: 8,
			maximumTotalBytes: 16,
			maximumResponseCount: 4);

		Assert.Equal("1234567", await reader.ReadLineAsync(CancellationToken.None));
		Assert.Equal("1234567", await reader.ReadLineAsync(CancellationToken.None));
		await Assert.ThrowsAsync<GrokUsageSchemaException>(
			() => reader.ReadLineAsync(CancellationToken.None));
	}

	private static void AssertRequestSequence(string input)
	{
		string[] lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(3, lines.Length);
		string[] expectedMethods =
		[
			"initialize",
			"x.ai/auth/info",
			"x.ai/billing"
		];
		int[] expectedIds = [1, 4, 2];

		for (int index = 0; index < lines.Length; index++)
		{
			using JsonDocument request = JsonDocument.Parse(lines[index]);
			JsonElement root = request.RootElement;
			Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
			Assert.Equal(expectedIds[index], root.GetProperty("id").GetInt32());
			Assert.Equal(expectedMethods[index], root.GetProperty("method").GetString());
			Assert.True(root.TryGetProperty("params", out _));
		}

		string normalizedInput = input.ToLowerInvariant();
		Assert.DoesNotContain("session", normalizedInput, StringComparison.Ordinal);
		Assert.DoesNotContain("prompt", normalizedInput, StringComparison.Ordinal);
		Assert.DoesNotContain("model", normalizedInput, StringComparison.Ordinal);
		Assert.DoesNotContain("tool", normalizedInput, StringComparison.Ordinal);
	}

	private static string CreateSuccessfulOutput(
		DateTimeOffset now,
		double? creditUsagePercent,
		bool useLegacyAuthNamespace = false,
		bool useLegacyBillingNamespace = false,
		string? notification = null,
		string? accountEmail = null,
		bool isUnifiedBillingUser = true,
		string? periodType = "USAGE_PERIOD_TYPE_WEEKLY",
		object? subscriptionTier = null)
	{
		Dictionary<string, object?> config = new()
		{
			["isUnifiedBillingUser"] = isUnifiedBillingUser,
			["currentPeriod"] = new
			{
				type = periodType,
				start = (now - TimeSpan.FromDays(3)).ToString("O"),
				end = (now + TimeSpan.FromDays(4)).ToString("O")
			}
		};

		if (creditUsagePercent is not null)
		{
			config["creditUsagePercent"] = creditUsagePercent.Value;
		}

		Dictionary<string, object?> billingResult = new()
		{
			["config"] = config
		};
		if (subscriptionTier is not null)
		{
			billingResult["subscriptionTier"] = subscriptionTier;
		}

		List<string> responses =
		[
			CreateResultResponse(1, new { protocolVersion = 1 }),
			notification ?? CreateNotification("_x.ai/mcp/servers_updated")
		];

		Dictionary<string, object?> principal = new()
		{
			["principalType"] = "consumer",
			["principalId"] = "opaque-principal"
		};

		if (accountEmail is not null)
		{
			principal["email"] = accountEmail;
		}

		if (useLegacyAuthNamespace)
		{
			responses.Add(CreateErrorResponse(4, -32601, "Method not found"));
			responses.Add(CreateResultResponse(5, principal));
		}
		else
		{
			responses.Add(CreateResultResponse(4, principal));
		}

		if (useLegacyBillingNamespace)
		{
			responses.Add(CreateErrorResponse(2, -32601, "Method not found"));
			responses.Add(CreateResultResponse(3, billingResult));
		}
		else
		{
			responses.Add(CreateResultResponse(2, billingResult));
		}

		return string.Join('\n', responses) + "\n";
	}

	private static string CreatePrincipalResultResponse(
		int id,
		string principalId = "opaque-principal")
	{
		return CreateResultResponse(id, new
		{
			principalType = "consumer",
			principalId
		});
	}

	private static string CreateResultResponse(int id, object result)
	{
		return JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id,
			result
		});
	}

	private static string CreateErrorResponse(
		int id,
		long code,
		string message,
		string? data = null)
	{
		Dictionary<string, object?> error = new()
		{
			["code"] = code,
			["message"] = message
		};

		if (data is not null)
		{
			error["data"] = data;
		}

		return JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id,
			error
		});
	}

	private static string CreateNotification(string method)
	{
		return JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method,
			@params = new { }
		});
	}

	private static GrokAcpLaunchOptions CreateLaunchOptions()
	{
		string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "grok-acp-test"));
		return new GrokAcpLaunchOptions(
			Path.Combine(root, "grok.exe"),
			Path.Combine(root, "home"),
			Path.Combine(root, "work"));
	}

	private static string[] ReadRequestMethods(string input)
	{
		return input
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line =>
			{
				using JsonDocument request = JsonDocument.Parse(line);
				return request.RootElement.GetProperty("method").GetString()!;
			})
			.ToArray();
	}
}
