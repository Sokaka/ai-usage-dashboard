using System.Diagnostics;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CodexAppServerUsagePollerTests
{
	private sealed class FakeCodexWorkspaceBindingStore :
		ICodexWorkspaceBindingStore
	{
		private readonly Dictionary<Guid, CodexWorkspaceBinding> _bindings;
		private readonly Exception? _loadAllException;
		private readonly Exception? _loadException;

		internal FakeCodexWorkspaceBindingStore(
			IEnumerable<CodexWorkspaceBinding> bindings,
			Exception? loadException = null,
			Exception? loadAllException = null)
		{
			_bindings = bindings.ToDictionary(binding => binding.AccountId);
			_loadException = loadException;
			_loadAllException = loadAllException;
		}

		public Task<IReadOnlyList<CodexWorkspaceBinding>> LoadAllAsync(
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_loadAllException is not null)
			{
				return Task.FromException<IReadOnlyList<CodexWorkspaceBinding>>(
					_loadAllException);
			}
			return Task.FromResult<IReadOnlyList<CodexWorkspaceBinding>>(
				_bindings.Values.ToArray());
		}

		public Task<CodexWorkspaceBinding?> LoadAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_loadException is not null)
			{
				return Task.FromException<CodexWorkspaceBinding?>(_loadException);
			}
			_bindings.TryGetValue(accountId, out CodexWorkspaceBinding? binding);
			return Task.FromResult(binding);
		}

		public Task SaveAsync(
			CodexWorkspaceBinding binding,
			CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public Task DeleteAsync(
			Guid accountId,
			CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}
	}

	private sealed class DisposalProbe : IDisposable
	{
		private readonly TaskCompletionSource _disposed = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal Task Disposed => _disposed.Task;

		public void Dispose()
		{
			_disposed.TrySetResult();
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

	private sealed class FakeTransport : ICodexAppServerTransport
	{
		private readonly Queue<string> _automaticResponses = new();
		private readonly bool _blockReads;
		private readonly Task _disposeBlock;
		private readonly Exception? _readFailure;
		private readonly Queue<string> _responses;
		private readonly TaskCompletionSource _abortObserved = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _disposeStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _readStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		public ProcessStartInfo StartInfo { get; }

		internal bool IsDisposed { get; private set; }

		internal bool IsAborted { get; private set; }

		internal Task AbortObserved => _abortObserved.Task;

		internal Task DisposeStarted => _disposeStarted.Task;

		internal Task ReadStarted => _readStarted.Task;

		internal List<string> WrittenMessages { get; } = new();

		internal FakeTransport(
			ProcessStartInfo startInfo,
			IEnumerable<string>? responses = null,
			bool blockReads = false,
			Task? disposeBlock = null,
			Exception? readFailure = null)
		{
			StartInfo = startInfo;
			_responses = new Queue<string>(responses ?? Array.Empty<string>());
			_blockReads = blockReads;
			_disposeBlock = disposeBlock ?? Task.CompletedTask;
			_readFailure = readFailure;
		}

		public void Abort()
		{
			IsAborted = true;
			_abortObserved.TrySetResult();
		}

		public async ValueTask<string?> ReadLineAsync(
			CancellationToken cancellationToken)
		{
			if (_readFailure is not null)
			{
				throw _readFailure;
			}

			if (_blockReads)
			{
				_readStarted.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();
			if (_automaticResponses.Count > 0)
			{
				return _automaticResponses.Dequeue();
			}

			return _responses.Count == 0 ? null : _responses.Dequeue();
		}

		public ValueTask WriteLineAsync(
			string message,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			WrittenMessages.Add(message);
			QueueAutomaticConfigReadResponse(message);
			return ValueTask.CompletedTask;
		}

		private void QueueAutomaticConfigReadResponse(string message)
		{
			using JsonDocument request = JsonDocument.Parse(message);
			JsonElement root = request.RootElement;
			if ((!root.TryGetProperty("method", out JsonElement method)) ||
				(!string.Equals(method.GetString(), "config/read", StringComparison.Ordinal)) ||
				(!root.TryGetProperty("id", out JsonElement id)) ||
				(!id.TryGetInt32(out int requestId)) ||
				_responses.Any(response => IsResponseForRequest(response, requestId)))
			{
				return;
			}

			_automaticResponses.Enqueue(JsonSerializer.Serialize(new
			{
				id = requestId,
				result = new
				{
					config = new
					{
						forced_chatgpt_workspace_id = (string?)null
					},
					origins = new { }
				}
			}));
		}

		private static bool IsResponseForRequest(string response, int requestId)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(response);
				return document.RootElement.TryGetProperty("id", out JsonElement id) &&
					id.TryGetInt32(out int responseId) &&
					responseId == requestId;
			}
			catch (JsonException)
			{
				return false;
			}
		}

		public async ValueTask DisposeAsync()
		{
			IsDisposed = true;
			_disposeStarted.TrySetResult();
			await _disposeBlock;
		}
	}

	private static readonly DateTimeOffset ObservedAt =
		new(2026, 7, 15, 6, 30, 0, TimeSpan.Zero);
	private static readonly Guid WorkspaceId =
		Guid.Parse("2d4f2e6c-cbad-45bb-a65c-11d21b67c73b");

	[Fact]
	public async Task PollAsync_WhenStableResponsesAreValid_MapsMultiBucketAndUsesReadOnlyProtocol()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		{
			"""{"id":1,"result":{"platformFamily":"windows","platformOs":"windows"}}""",
			"""{"method":"account/updated","params":{"authMode":"chatgpt","planType":"plus"}}""",
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			"""
			{"id":3,"result":{"rateLimits":{"limitId":"codex","limitName":null,"primary":{"usedPercent":1,"windowDurationMins":300,"resetsAt":1784100000},"secondary":null},"rateLimitsByLimitId":{"codex":{"limitId":"codex","limitName":"Codex","primary":{"usedPercent":21,"windowDurationMins":300,"resetsAt":1784100000},"secondary":{"usedPercent":44,"windowDurationMins":10080,"resetsAt":1784704800}},"review":{"limitId":"review","limitName":"Code Review","primary":{"usedPercent":12,"windowDurationMins":1440,"resetsAt":1784186400},"secondary":null}},"rateLimitResetCredits":{"availableCount":2,"credits":null}}}
			"""
		};
		FakeTransport? transport = null;
		CliVersionEvidence versionEvidence = CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0));
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			new CodexCliExecutableResolution(
				executablePath,
				versionEvidence),
			startInfo => transport = new FakeTransport(startInfo, responses),
			new FakeTimeProvider(ObservedAt));

		CodexUsagePollResult result = await poller.PollAsync(
			Guid.NewGuid(),
			CancellationToken.None);

		Assert.Equal("plus", result.PlanType);
		Assert.Equal("user@example.com", result.AccountIdentity);
		Assert.Equal(ObservedAt, result.ObservedAt);
		Assert.Same(versionEvidence, result.VersionEvidence);
		Assert.Equal(2, result.RateLimits.Count);
		Assert.Equal("codex", result.RateLimits[0].LimitId);
		Assert.Equal(21, result.RateLimits[0].Primary?.UsedPercent);
		Assert.Equal(TimeSpan.FromMinutes(300),
			TimeSpan.FromMinutes(result.RateLimits[0].Primary!.WindowDurationMinutes!.Value));
		Assert.Equal("review", result.RateLimits[1].LimitId);
		Assert.Equal(2, result.AvailableResetCredits);
		Assert.NotNull(transport);
		Assert.False(transport.IsAborted);
		Assert.True(transport.IsDisposed);
		Assert.Equal(5, transport.WrittenMessages.Count);
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"account/read",
				"account/rateLimits/read"
			},
			transport.WrittenMessages.Select(GetMethod));
		Assert.DoesNotContain(
			transport.WrittenMessages,
			message => message.Contains("thread/", StringComparison.Ordinal) ||
				message.Contains("turn/", StringComparison.Ordinal));
		using JsonDocument rateLimitsRequest = JsonDocument.Parse(
			transport.WrittenMessages[4]);
		Assert.False(rateLimitsRequest.RootElement.TryGetProperty("params", out _));
		using JsonDocument accountReadRequest = JsonDocument.Parse(
			transport.WrittenMessages[3]);
		Assert.False(accountReadRequest.RootElement
			.GetProperty("params")
			.GetProperty("refreshToken")
			.GetBoolean());
		Assert.Equal(temporaryDirectory.Path,
			transport.StartInfo.Environment["CODEX_HOME"]);
		Assert.Equal(temporaryDirectory.Path,
			transport.StartInfo.Environment["CODEX_SQLITE_HOME"]);
		Assert.False(transport.StartInfo.UseShellExecute);
		Assert.True(transport.StartInfo.RedirectStandardInput);
		Assert.Equal(
			new[] { "app-server", "--listen", "stdio://" },
			transport.StartInfo.ArgumentList);
	}

	[Fact]
	public async Task PollAsync_WhenForcedWorkspaceIsConfigured_RejectsAccountLevelRead()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		[
			"""{"id":1,"result":{}}""",
			CreateConfigReadResponse(WorkspaceId.ToString("D"))
		];
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo => transport = new FakeTransport(startInfo, responses),
			new FakeTimeProvider(ObservedAt));

		await Assert.ThrowsAsync<CodexWorkspaceMismatchException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.NotNull(transport);
		Assert.Equal(
			new[] { "initialize", "initialized", "config/read" },
			transport.WrittenMessages.Select(GetMethod));
	}

	[Fact]
	public async Task PollBoundAsync_WithMatchingTuple_VerifiesBeforeReadingRateLimits()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		Guid publicBindingId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			publicBindingId,
			"user@example.com",
			WorkspaceId);
		string publicBindingIdentity =
			CodexWorkspaceBinding.CreatePublicBindingIdentity(publicBindingId);
		string[] responses =
		[
			"""{"id":1,"result":{}}""",
			CreateConfigReadResponse(WorkspaceId.ToString("D")),
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":" User@Example.COM ","planType":"team"},"requiresOpenaiAuth":true}}""",
			"""{"id":3,"result":{"rateLimits":{"limitId":"codex","limitName":"Codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1784100000},"secondary":null},"rateLimitsByLimitId":null,"rateLimitResetCredits":null}}"""
		];
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			responses,
			new FakeCodexWorkspaceBindingStore([binding]),
			startInfo => transport = new FakeTransport(startInfo, responses));

		CodexUsagePollResult result = await poller.PollBoundAsync(
			accountId,
			publicBindingIdentity,
			CancellationToken.None);

		Assert.Equal("team", result.PlanType);
		Assert.Equal("user@example.com", result.AccountIdentity);
		Assert.Equal(publicBindingIdentity, result.PublicBindingIdentity);
		Assert.Equal(WorkspaceId, result.WorkspaceId);
		Assert.NotNull(transport);
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"account/read",
				"account/rateLimits/read"
			},
			transport.WrittenMessages.Select(GetMethod));
		using JsonDocument configReadRequest = JsonDocument.Parse(
			transport.WrittenMessages[2]);
		Assert.False(configReadRequest.RootElement
			.GetProperty("params")
			.GetProperty("includeLayers")
			.GetBoolean());
	}

	[Fact]
	public async Task PollBoundAsync_WhenBindingIsMissing_FailsBeforeStartingProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		int transportFactoryCallCount = 0;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			[],
			new FakeCodexWorkspaceBindingStore([]),
			startInfo =>
			{
				Interlocked.Increment(ref transportFactoryCallCount);
				return new FakeTransport(startInfo);
			});

		await Assert.ThrowsAsync<CodexWorkspaceBindingMissingException>(() =>
			poller.PollBoundAsync(
				Guid.NewGuid(),
				Guid.NewGuid().ToString("N")));

		Assert.Equal(0, Volatile.Read(ref transportFactoryCallCount));
	}

	[Fact]
	public async Task PollBoundAsync_WhenCurrentBindingIsMalformed_RequiresReconnect()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		int transportFactoryCallCount = 0;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			[],
			new FakeCodexWorkspaceBindingStore(
				[],
				loadException: new InvalidDataException("malformed current binding")),
			startInfo =>
			{
				Interlocked.Increment(ref transportFactoryCallCount);
				return new FakeTransport(startInfo);
			});

		await Assert.ThrowsAsync<CodexWorkspaceBindingInvalidException>(() =>
			poller.PollBoundAsync(
				Guid.NewGuid(),
				Guid.NewGuid().ToString("N")));

		Assert.Equal(0, Volatile.Read(ref transportFactoryCallCount));
	}

	[Fact]
	public async Task PollBoundAsync_WhenBindingInventoryIsMalformed_RemainsTransient()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			"user@example.com",
			WorkspaceId);
		int transportFactoryCallCount = 0;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			[],
			new FakeCodexWorkspaceBindingStore(
				[binding],
				loadAllException: new InvalidDataException("malformed inventory")),
			startInfo =>
			{
				Interlocked.Increment(ref transportFactoryCallCount);
				return new FakeTransport(startInfo);
			});

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollBoundAsync(
				accountId,
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)));

		Assert.Equal(0, Volatile.Read(ref transportFactoryCallCount));
	}

	[Fact]
	public async Task PollBoundAsync_WhenObservedWorkspaceDiffers_DoesNotReadRateLimits()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			"user@example.com",
			WorkspaceId);
		string[] responses =
		[
			"""{"id":1,"result":{}}""",
			CreateConfigReadResponse(Guid.NewGuid().ToString("D")),
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"team"},"requiresOpenaiAuth":true}}""",
			"""{"id":3,"result":{}}"""
		];
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			responses,
			new FakeCodexWorkspaceBindingStore([binding]),
			startInfo => transport = new FakeTransport(startInfo, responses));

		await Assert.ThrowsAsync<CodexWorkspaceMismatchException>(() =>
			poller.PollBoundAsync(
				accountId,
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)));

		Assert.NotNull(transport);
		Assert.DoesNotContain(
			transport.WrittenMessages,
			message => string.Equals(
				GetMethod(message),
				"account/rateLimits/read",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task PollBoundAsync_WhenAnotherCardOwnsTuple_DoesNotReadRateLimits()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			"user@example.com",
			WorkspaceId);
		CodexWorkspaceBinding conflictingBinding = CodexWorkspaceBinding.Create(
			Guid.NewGuid(),
			"user@example.com",
			WorkspaceId);
		string[] responses =
		[
			"""{"id":1,"result":{}}""",
			CreateConfigReadResponse(WorkspaceId.ToString("D")),
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"team"},"requiresOpenaiAuth":true}}""",
			"""{"id":3,"result":{}}"""
		];
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			responses,
			new FakeCodexWorkspaceBindingStore(
				[binding, conflictingBinding]),
			startInfo => transport = new FakeTransport(startInfo, responses));

		await Assert.ThrowsAsync<CodexWorkspaceMismatchException>(() =>
			poller.PollBoundAsync(
				accountId,
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)));

		Assert.NotNull(transport);
		Assert.DoesNotContain(
			transport.WrittenMessages,
			message => string.Equals(
				GetMethod(message),
				"account/rateLimits/read",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task PollBoundAsync_WhenConfigContainsMultipleWorkspaces_FailsBeforeAccountRead()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			"user@example.com",
			WorkspaceId);
		string[] responses =
		[
			"""{"id":1,"result":{}}""",
			CreateConfigReadResponse(
				new[]
				{
					WorkspaceId.ToString("D"),
					Guid.NewGuid().ToString("D")
				}),
			"""{"id":2,"result":{}}"""
		];
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			responses,
			new FakeCodexWorkspaceBindingStore([binding]),
			startInfo => transport = new FakeTransport(startInfo, responses));

		await Assert.ThrowsAsync<CodexWorkspaceConfigurationException>(() =>
			poller.PollBoundAsync(
				accountId,
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)));

		Assert.NotNull(transport);
		Assert.DoesNotContain(
			transport.WrittenMessages,
			message => string.Equals(
				GetMethod(message),
				"account/read",
				StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("""{"id":6,"result":{"config":{},"origins":{}}}""")]
	[InlineData("""{"id":6,"result":{"config":{"forced_chatgpt_workspace_id":null},"origins":{}}}""")]
	[InlineData("""{"id":6,"result":{"config":{"forced_chatgpt_workspace_id":"not-a-uuid"},"origins":{}}}""")]
	[InlineData("""{"id":6,"result":{"config":{"forced_chatgpt_workspace_id":[]},"origins":{}}}""")]
	public async Task PollBoundAsync_WhenWorkspaceConfigIsMissingOrInvalid_FailsClosed(
		string configResponse)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		CodexWorkspaceBinding binding = CodexWorkspaceBinding.Create(
			accountId,
			"user@example.com",
			WorkspaceId);
		string[] responses =
		[
			"""{"id":1,"result":{}}""",
			configResponse,
			"""{"id":2,"result":{}}"""
		];
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = CreateBoundPoller(
			temporaryDirectory.Path,
			executablePath,
			responses,
			new FakeCodexWorkspaceBindingStore([binding]),
			startInfo => transport = new FakeTransport(startInfo, responses));

		await Assert.ThrowsAsync<CodexWorkspaceConfigurationException>(() =>
			poller.PollBoundAsync(
				accountId,
				CodexWorkspaceBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId)));

		Assert.NotNull(transport);
		Assert.DoesNotContain(
			transport.WrittenMessages,
			message => string.Equals(
				GetMethod(message),
				"account/read",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task PollAsync_WhenRateLimitsReportExpiredAccessToken_RefreshesAccountAndRetriesOnce()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		const string expiredTokenMessage = "401 Unauthorized: token_expired";
		string[] responses =
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"before-refresh@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			JsonSerializer.Serialize(new
			{
				id = 3,
				error = new
				{
					code = -32603,
					message = expiredTokenMessage
				}
			}),
			"""{"id":4,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			"""{"id":5,"result":{"rateLimits":{"limitId":"codex","limitName":"Codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1784100000},"secondary":null},"rateLimitsByLimitId":null,"rateLimitResetCredits":null}}"""
		};
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo => transport = new FakeTransport(startInfo, responses),
			new FakeTimeProvider(ObservedAt));

		CodexUsagePollResult result = await poller.PollAsync(
			Guid.NewGuid(),
			CancellationToken.None);

		CodexRateLimitBucket bucket = Assert.Single(result.RateLimits);
		Assert.Equal("codex", bucket.LimitId);
		Assert.Equal("user@example.com", result.AccountIdentity);
		Assert.NotNull(transport);
		Assert.False(transport.IsAborted);
		Assert.True(transport.IsDisposed);
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"account/read",
				"account/rateLimits/read",
				"account/read",
				"account/rateLimits/read"
			},
			transport.WrittenMessages.Select(GetMethod));
		Assert.Equal(
			new[] { false, true },
			transport.WrittenMessages
				.Where(message => string.Equals(
					GetMethod(message),
					"account/read",
					StringComparison.Ordinal))
				.Select(GetRefreshToken));
	}

	[Fact]
	public async Task PollAsync_WhenRateLimitsStillReportExpiredAccessTokenAfterRefresh_StopsAfterOneRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		const string expiredTokenMessage = "401 Unauthorized: token_expired";
		string expiredTokenResponse(int id) => JsonSerializer.Serialize(new
		{
			id,
			error = new
			{
				code = -32603,
				message = expiredTokenMessage
			}
		});
		string[] responses =
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			expiredTokenResponse(3),
			"""{"id":4,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			expiredTokenResponse(5)
		};
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo => transport = new FakeTransport(startInfo, responses),
			new FakeTimeProvider(ObservedAt));

		CodexAppServerFailureException exception =
			await Assert.ThrowsAsync<CodexAppServerFailureException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Equal(-32603, exception.ErrorCode);
		Assert.Equal(expiredTokenMessage, exception.ServerMessage);
		Assert.Equal(
			CodexAppServerFailureCategory.Authentication,
			exception.Category);
		Assert.NotNull(transport);
		Assert.True(transport.IsAborted);
		Assert.True(transport.IsDisposed);
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"account/read",
				"account/rateLimits/read",
				"account/read",
				"account/rateLimits/read"
			},
			transport.WrittenMessages.Select(GetMethod));
		Assert.Equal(
			new[] { false, true },
			transport.WrittenMessages
				.Where(message => string.Equals(
					GetMethod(message),
					"account/read",
					StringComparison.Ordinal))
				.Select(GetRefreshToken));
	}

	[Fact]
	public async Task PollAsync_WhenRateLimitsReportExpiredRefreshToken_DoesNotForceRefresh()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			"""{"id":3,"error":{"code":-32603,"message":"The refresh_token expired: token_expired."}}"""
		};
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo => transport = new FakeTransport(startInfo, responses),
			new FakeTimeProvider(ObservedAt));

		CodexAppServerFailureException exception =
			await Assert.ThrowsAsync<CodexAppServerFailureException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Equal(
			CodexAppServerFailureCategory.Authentication,
			exception.Category);
		Assert.NotNull(transport);
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"account/read",
				"account/rateLimits/read"
			},
			transport.WrittenMessages.Select(GetMethod));
		Assert.Equal(
			new[] { false },
			transport.WrittenMessages
				.Where(message => string.Equals(
					GetMethod(message),
					"account/read",
					StringComparison.Ordinal))
				.Select(GetRefreshToken));
	}

	[Fact]
	public async Task PollAsync_WhenMultiBucketIsNull_UsesLegacyRateLimits()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeTransport? transport = null;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo => transport = new FakeTransport(
				startInfo,
				CreateResponses(
					"""{"limitId":"other","limitName":null,"primary":{"usedPercent":37,"windowDurationMins":300,"resetsAt":1784100000},"secondary":null}""",
					"null")),
			new FakeTimeProvider(ObservedAt));

		CodexUsagePollResult result = await poller.PollAsync(
			Guid.NewGuid(),
			CancellationToken.None);

		CodexRateLimitBucket bucket = Assert.Single(result.RateLimits);
		Assert.Equal("user@example.com", result.AccountIdentity);
		Assert.Equal("other", bucket.LimitId);
		Assert.Equal(37, bucket.Primary?.UsedPercent);
		Assert.NotNull(transport);
	}

	[Fact]
	public async Task PollAsync_WhenRateLimitTextHasOuterWhitespace_TrimsValues()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string legacyRateLimits = JsonSerializer.Serialize(
			CreateRateLimitBucketPayload(" codex ", " Codex "));
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			CreateResponses(legacyRateLimits, "null"));

		CodexUsagePollResult result = await poller.PollAsync(
			Guid.NewGuid(),
			CancellationToken.None);

		CodexRateLimitBucket bucket = Assert.Single(result.RateLimits);
		Assert.Equal("codex", bucket.LimitId);
		Assert.Equal("Codex", bucket.LimitName);
	}

	[Fact]
	public async Task PollAsync_WhenLimitIdIsEmpty_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string legacyRateLimits = JsonSerializer.Serialize(
			CreateRateLimitBucketPayload(" ", "Codex"));
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			CreateResponses(legacyRateLimits, "null"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Theory]
	[InlineData("limitId")]
	[InlineData("limitName")]
	public async Task PollAsync_WhenRateLimitTextIsTooLong_FailsClosed(
		string fieldName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string invalidText = fieldName == "limitId"
			? new string('a', 185)
			: new string('a', 174);
		string limitId = fieldName == "limitId" ? invalidText : "codex";
		string limitName = fieldName == "limitName" ? invalidText : "Codex";
		string legacyRateLimits = JsonSerializer.Serialize(
			CreateRateLimitBucketPayload(limitId, limitName));
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			CreateResponses(legacyRateLimits, "null"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Theory]
	[InlineData("limitId")]
	[InlineData("limitName")]
	public async Task PollAsync_WhenRateLimitTextContainsControlCharacter_FailsClosed(
		string fieldName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		const string invalidText = "Codex\u0007Usage";
		string limitId = fieldName == "limitId" ? invalidText : "codex";
		string limitName = fieldName == "limitName" ? invalidText : "Codex";
		string legacyRateLimits = JsonSerializer.Serialize(
			CreateRateLimitBucketPayload(limitId, limitName));
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			CreateResponses(legacyRateLimits, "null"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task PollAsync_WhenMoreThan32BucketsAreReturned_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Dictionary<string, object> buckets = Enumerable.Range(0, 33)
			.ToDictionary(
				index => $"bucket-{index}",
				index => CreateRateLimitBucketPayload($"bucket-{index}"));
		string multiBucketRateLimits = JsonSerializer.Serialize(buckets);
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			CreateResponses(
				JsonSerializer.Serialize(CreateRateLimitBucketPayload("codex")),
				multiBucketRateLimits));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task PollAsync_WhenResetCreditsWouldCreate65Metrics_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Dictionary<string, object> buckets = Enumerable.Range(0, 32)
			.ToDictionary(
				index => $"bucket-{index}",
				index => CreateRateLimitBucketPayload(
					$"bucket-{index}",
					includeSecondary: true));
		string multiBucketRateLimits = JsonSerializer.Serialize(buckets);
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			CreateResponses(
				JsonSerializer.Serialize(CreateRateLimitBucketPayload("codex")),
				multiBucketRateLimits,
				"{\"availableCount\":1}"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task PollAsync_WhenAccountIsMissingAndOpenAiAuthIsRequired_ReturnsNotConfiguredReason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":null,"requiresOpenaiAuth":true}}"""
		};
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			responses);

		CodexUsageNotConfiguredException exception =
			await Assert.ThrowsAsync<CodexUsageNotConfiguredException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Contains("尚未登入", exception.Message);
	}

	[Fact]
	public async Task PollAsync_WhenAccountIsMissingWithoutOpenAiAuthRequirement_RequiresProviderChange()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":null,"requiresOpenaiAuth":false}}"""
		};
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			responses);

		CodexUsageAccountActionRequiredException exception =
			await Assert.ThrowsAsync<CodexUsageAccountActionRequiredException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Contains("ChatGPT 登入", exception.Message);
		Assert.Contains("重新連接", exception.Message);
	}

	[Fact]
	public async Task PollAsync_WhenChatGptAccountHasNoEmail_RequiresDifferentAccount()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":null,"planType":"enterprise"},"requiresOpenaiAuth":true}}"""
		};
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			responses);

		CodexUsageAccountActionRequiredException exception =
			await Assert.ThrowsAsync<CodexUsageAccountActionRequiredException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Contains("無法確認目前登入的帳號", exception.Message);
		Assert.Contains("其他 ChatGPT 帳號", exception.Message);
	}

	[Fact]
	public async Task PollAsync_WhenAccountUsesApiKey_RejectsSubscriptionMapping()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":{"type":"apiKey"},"requiresOpenaiAuth":false}}"""
		};
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			responses);

		CodexUsageNotConfiguredException exception =
			await Assert.ThrowsAsync<CodexUsageNotConfiguredException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Contains("ChatGPT", exception.Message);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(101)]
	public async Task PollAsync_WhenUsedPercentIsInvalid_FailsClosed(int usedPercent)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string legacyRateLimits =
			$$"""{"limitId":"codex","primary":{"usedPercent":{{usedPercent}},"windowDurationMins":300,"resetsAt":1784100000},"secondary":null}""";
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			CreateResponses(legacyRateLimits, "null"));

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task PollAsync_WhenResponseContainsDuplicateProperty_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string[] responses =
		{
			"""{"id":1,"id":1,"result":{}}"""
		};
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			responses);

		await Assert.ThrowsAsync<InvalidDataException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task PollAsync_WhenCallerCancels_PropagatesCancellationAndDisposesTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		CliVersionEvidence versionEvidence = CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0));
		TaskCompletionSource<FakeTransport> transportCreated = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			new CodexCliExecutableResolution(
				executablePath,
				versionEvidence),
			startInfo =>
			{
				FakeTransport transport = new(startInfo, blockReads: true);
				transportCreated.TrySetResult(transport);
				return transport;
			},
			new FakeTimeProvider(ObservedAt));
		using CancellationTokenSource cancellationSource = new();
		Task<CodexUsagePollResult> pollTask = poller.PollAsync(
			Guid.NewGuid(),
			cancellationSource.Token);
		FakeTransport transport = await transportCreated.Task.WaitAsync(
			TimeSpan.FromSeconds(5));
		await transport.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
		cancellationSource.Cancel();

		OperationCanceledException exception =
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				pollTask);
		Assert.Equal("原始取消訊息。", exception.AppendVersionDiagnostic("原始取消訊息。"));
		await transport.AbortObserved.WaitAsync(TimeSpan.FromSeconds(5));
		await transport.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(transport.IsAborted);
		Assert.True(transport.IsDisposed);
	}

	[Fact]
	public async Task PollAsync_WhenExecutableIsMissing_DoesNotStartTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		bool transportStarted = false;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => Path.Combine(temporaryDirectory.Path, "missing-codex.exe"),
			startInfo =>
			{
				transportStarted = true;
				return new FakeTransport(startInfo);
			},
			new FakeTimeProvider(ObservedAt),
			operationGate);

		await Assert.ThrowsAsync<CodexCliNotFoundException>(() =>
			poller.PollAsync(accountId, CancellationToken.None));
		Assert.False(transportStarted);
		using CancellationTokenSource cancellationSource = new(
			TimeSpan.FromSeconds(1));
		using IDisposable lease = await operationGate.EnterAsync(
			accountId,
			cancellationSource.Token);
	}

	[Theory]
	[InlineData(
		-32603L,
		"Your access token could not be refreshed because your refresh token was revoked. Please sign in again.",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32603L,
		"OAuth refresh failed with invalid_grant because the refresh_token was already used.",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32603L,
		"The refresh token has expired.",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32603L,
		"The refresh token is invalid.",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32603L,
		"The refresh token expired.",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32603L,
		"The refresh_token invalid.",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32603L,
		"The expired refresh_token cannot be reused.",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32603L,
		"401 Unauthorized: token_expired",
		nameof(CodexAppServerFailureCategory.Authentication))]
	[InlineData(
		-32099L,
		"Refresh token request timed out while contacting the authentication service.",
		nameof(CodexAppServerFailureCategory.Transient))]
	[InlineData(
		-32099L,
		"401 Unauthorized: refresh token service is temporarily unavailable.",
		nameof(CodexAppServerFailureCategory.Transient))]
	[InlineData(
		-32099L,
		"Refresh token service returned an invalid response.",
		nameof(CodexAppServerFailureCategory.Unknown))]
	[InlineData(
		-32099L,
		"Refresh token request expired while service unavailable.",
		nameof(CodexAppServerFailureCategory.Transient))]
	[InlineData(
		-32601L,
		"Method not found",
		nameof(CodexAppServerFailureCategory.Compatibility))]
	[InlineData(
		-32601L,
		"Method not found while the service is temporarily unavailable",
		nameof(CodexAppServerFailureCategory.Compatibility))]
	[InlineData(
		-32700L,
		"Parse error",
		nameof(CodexAppServerFailureCategory.Compatibility))]
	[InlineData(
		-32600L,
		"Invalid request",
		nameof(CodexAppServerFailureCategory.Compatibility))]
	[InlineData(
		-32602L,
		"Invalid params",
		nameof(CodexAppServerFailureCategory.Compatibility))]
	[InlineData(
		-32700L,
		"Request parsing is temporarily unavailable",
		nameof(CodexAppServerFailureCategory.Transient))]
	[InlineData(
		-32600L,
		"Invalid request; try again",
		nameof(CodexAppServerFailureCategory.Transient))]
	[InlineData(
		-32602L,
		"Invalid params because the service is temporarily unavailable",
		nameof(CodexAppServerFailureCategory.Transient))]
	[InlineData(
		-32001L,
		"Server overloaded; retry later.",
		nameof(CodexAppServerFailureCategory.Transient))]
	[InlineData(
		-32099L,
		"Unexpected app-server failure",
		nameof(CodexAppServerFailureCategory.Unknown))]
	public async Task PollAsync_WhenAppServerReturnsError_PreservesAndClassifiesFailure(
		long errorCode,
		string serverMessage,
		string expectedCategory)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string response = JsonSerializer.Serialize(new
		{
			id = 1,
			error = new
			{
				code = errorCode,
				message = serverMessage
			}
		});
		CodexAppServerUsagePoller poller = CreatePoller(
			temporaryDirectory.Path,
			executablePath,
			new[] { response });

		CodexAppServerFailureException exception =
			await Assert.ThrowsAsync<CodexAppServerFailureException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Equal(errorCode, exception.ErrorCode);
		Assert.Equal(serverMessage, exception.ServerMessage);
		Assert.Equal(expectedCategory, exception.Category.ToString());
	}

	[Fact]
	public async Task PollAsync_WithUnverifiedVersion_AttachesEvidenceToFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		CliVersionEvidence versionEvidence = CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0));
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			new CodexCliExecutableResolution(
				executablePath,
				versionEvidence),
			startInfo => new FakeTransport(
				startInfo,
				[
					"""{"id":1,"error":{"code":-32601,"message":"Method not found"}}"""
				]),
			new FakeTimeProvider(ObservedAt));

		CodexAppServerFailureException exception =
			await Assert.ThrowsAsync<CodexAppServerFailureException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		string message = exception.AppendVersionDiagnostic("原始錯誤。");
		Assert.Contains("Codex CLI 0.145.0", message, StringComparison.Ordinal);
		Assert.Contains("相容性基準為 0.144.1", message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PollAsync_WhenTransportReadFails_DoesNotAttachVersionEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		CliVersionEvidence versionEvidence = CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0));
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			new CodexCliExecutableResolution(
				executablePath,
				versionEvidence),
			startInfo => new FakeTransport(
				startInfo,
				readFailure: new IOException("local stream failure")),
			new FakeTimeProvider(ObservedAt));

		IOException exception = await Assert.ThrowsAsync<IOException>(() =>
			poller.PollAsync(Guid.NewGuid(), CancellationToken.None));

		Assert.Equal(
			"原始錯誤。",
			exception.AppendVersionDiagnostic("原始錯誤。"));
	}

	[Fact]
	public async Task PollAsync_WhenHomeIsMissing_DoesNotCreateDirectory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string missingHome = Path.Combine(temporaryDirectory.Path, "missing-home");
		CliVersionEvidence versionEvidence = CliVersionPolicies.AssessCodex(
			new Version(0, 145, 0));
		CodexAppServerUsagePoller poller = new(
			_ => missingHome,
			new CodexCliExecutableResolution(
				executablePath,
				versionEvidence),
			startInfo => new FakeTransport(startInfo),
			new FakeTimeProvider(ObservedAt));

		DirectoryNotFoundException exception =
			await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
				poller.PollAsync(Guid.NewGuid(), CancellationToken.None));
		Assert.Equal("原始未設定訊息。", exception.AppendVersionDiagnostic("原始未設定訊息。"));
		Assert.False(Directory.Exists(missingHome));
	}

	[Fact]
	public async Task CreateTransportAsync_WhenAlreadyCancelled_DoesNotInvokeFactory()
	{
		using CancellationTokenSource cancellationSource = new();
		cancellationSource.Cancel();
		int factoryCallCount = 0;
		ProcessStartInfo startInfo = new();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			CodexAppServerUsagePoller.CreateTransportAsync(
				info =>
				{
					Interlocked.Increment(ref factoryCallCount);
					return new FakeTransport(info);
				},
				startInfo,
				cancellationSource.Token));

		Assert.Equal(0, Volatile.Read(ref factoryCallCount));
	}

	[Fact]
	public async Task ProcessTransport_WhenInitializationFailsAfterProcessStart_TracksCleanupBeforeReleasingExecutableLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		ProviderProcessOperationTracker operationTracker = new();
		IDisposable? trackedExecutableLease =
			operationTracker.HoldLease(executableLease);
		TaskCompletionSource releaseCleanup = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		Task? cleanupTask = null;
		string commandPath = Environment.GetEnvironmentVariable("ComSpec") ??
			throw new InvalidOperationException("ComSpec is unavailable.");
		ProcessStartInfo startInfo = new()
		{
			CreateNoWindow = true,
			FileName = commandPath,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = false,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("ping.exe -n 30 127.0.0.1 >nul");

		async Task CompleteCleanupAsync(Process process)
		{
			await releaseCleanup.Task;

			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}

				await process.WaitForExitAsync();
			}
			finally
			{
				process.Dispose();
			}
		}

		try
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				CodexAppServerUsagePoller.CreateTransportAsync(
					(info, tracker) =>
						new CodexAppServerUsagePoller.ProcessTransport(
							info,
							tracker,
							process => cleanupTask =
								CompleteCleanupAsync(process)),
					startInfo,
					CancellationToken.None,
					operationTracker));
			Assert.NotNull(cleanupTask);
			trackedExecutableLease.Dispose();
			trackedExecutableLease = null;

			Assert.True(executableLease.IsProtected);
			Assert.Throws<IOException>(() =>
			{
				using FileStream writer = new(
					executablePath,
					FileMode.Open,
					FileAccess.Write,
					FileShare.None);
			});

			releaseCleanup.TrySetResult();
			await cleanupTask!.WaitAsync(TimeSpan.FromSeconds(5));
			await WaitUntilAsync(
				() => !executableLease.IsProtected,
				TimeSpan.FromSeconds(5));
			using FileStream writer = new(
				executablePath,
				FileMode.Open,
				FileAccess.Write,
				FileShare.None);
		}
		finally
		{
			releaseCleanup.TrySetResult();

			if (cleanupTask is not null)
			{
				await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5));
			}

			trackedExecutableLease?.Dispose();
			executableLease.Dispose();
		}
	}

	[Fact]
	public async Task ProcessTransport_WhenStandardInputCloseThrows_StillCompletesCleanup()
	{
		ProviderProcessOperationTracker operationTracker = new();
		string commandPath = Environment.GetEnvironmentVariable("ComSpec") ??
			throw new InvalidOperationException("ComSpec is unavailable.");
		ProcessStartInfo startInfo = new()
		{
			CreateNoWindow = true,
			FileName = commandPath,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("exit /b 0");
		int closeAttemptCount = 0;
		await using CodexAppServerUsagePoller.ProcessTransport transport = new(
			startInfo,
			operationTracker,
			process =>
			{
				process.Dispose();
				return Task.CompletedTask;
			},
			_ =>
			{
				Interlocked.Increment(ref closeAttemptCount);
				throw new IOException("Injected stdin close failure.");
			});

		await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, Volatile.Read(ref closeAttemptCount));
	}

	[Fact]
	public async Task ProcessTransportCleanup_WhenTreeKillPartiallyFails_QuarantinesExecutableLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		ProviderProcessOperationTracker operationTracker = new();
		IDisposable? trackedExecutableLease =
			operationTracker.HoldLease(executableLease);
		TaskCompletionSource cleanupCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DisposalProbe processResource = new();
		int terminateCount = 0;

		try
		{
			await CodexAppServerUsagePoller.ProcessTransport
				.CompleteBoundedCleanupAsync(
					cleanupCompletion.Task,
					() =>
					{
						Interlocked.Increment(ref terminateCount);
						throw new AggregateException("Injected kill failure.");
					},
					processResource,
					operationTracker,
					TimeSpan.FromMilliseconds(25))
				.WaitAsync(TimeSpan.FromSeconds(5));
			trackedExecutableLease.Dispose();
			trackedExecutableLease = null;

			Assert.Equal(1, Volatile.Read(ref terminateCount));
			Assert.False(processResource.Disposed.IsCompleted);
			Assert.True(executableLease.IsProtected);
			Assert.Throws<IOException>(() =>
			{
				using FileStream writer = new(
					executablePath,
					FileMode.Open,
					FileAccess.Write,
					FileShare.None);
			});

			cleanupCompletion.TrySetResult();
			await processResource.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(executableLease.IsProtected);
			Assert.Throws<IOException>(() =>
			{
				using FileStream writer = new(
					executablePath,
					FileMode.Open,
					FileAccess.Write,
					FileShare.None);
			});
		}
		finally
		{
			cleanupCompletion.TrySetResult();
			await processResource.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
			trackedExecutableLease?.Dispose();
			executableLease.Dispose();
		}
	}

	[Fact]
	public async Task ProcessTransportCleanup_WhenExitCannotBeConfirmed_MarksContainmentCompromised()
	{
		ProviderProcessOperationTracker operationTracker = new();
		TaskCompletionSource cleanupCompletion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		DisposalProbe processResource = new();
		int terminateCount = 0;

		try
		{
			await CodexAppServerUsagePoller.ProcessTransport
				.CompleteBoundedCleanupAsync(
					cleanupCompletion.Task,
					() => Interlocked.Increment(ref terminateCount),
					processResource,
					operationTracker,
					TimeSpan.FromMilliseconds(25))
				.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.Equal(1, Volatile.Read(ref terminateCount));
			Assert.True(operationTracker.IsContainmentCompromised);
			Assert.False(processResource.Disposed.IsCompleted);
		}
		finally
		{
			cleanupCompletion.TrySetResult();
			await processResource.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	[Fact]
	public async Task PollAsync_WhenTransportFactoryBlocks_TimesOutAndCleansUpLateTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		using ManualResetEventSlim releaseFactory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		TaskCompletionSource factoryEntered = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<FakeTransport> transportCreated = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo =>
			{
				factoryEntered.TrySetResult();
				releaseFactory.Wait();
				FakeTransport transport = new(startInfo);
				transportCreated.TrySetResult(transport);
				return transport;
			},
			new FakeTimeProvider(ObservedAt),
			new CodexAccountOperationGate(),
			TimeSpan.FromMilliseconds(250));
		Task<CodexUsagePollResult> pollTask = poller.PollAsync(
			Guid.NewGuid(),
			CancellationToken.None);

		try
		{
			await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await Assert.ThrowsAsync<TimeoutException>(() => pollTask)
				.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(transportCreated.Task.IsCompleted);

			releaseFactory.Set();
			FakeTransport transport = await transportCreated.Task.WaitAsync(
				TimeSpan.FromSeconds(5));
			await transport.AbortObserved.WaitAsync(TimeSpan.FromSeconds(5));
			await transport.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(transport.IsAborted);
			Assert.True(transport.IsDisposed);
		}
		finally
		{
			releaseFactory.Set();
		}
	}

	[Fact]
	public async Task PollAsync_WhenTimeoutCleanupBlocks_ReturnsBeforeCleanupCompletes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		TaskCompletionSource releaseDispose = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<FakeTransport> transportCreated = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		CodexAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		int factoryCallCount = 0;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo =>
			{
				Interlocked.Increment(ref factoryCallCount);
				FakeTransport transport = new(
					startInfo,
					blockReads: true,
					disposeBlock: releaseDispose.Task);
				transportCreated.TrySetResult(transport);
				return transport;
			},
			new FakeTimeProvider(ObservedAt),
			operationGate,
			TimeSpan.FromMilliseconds(250));

		try
		{
			Task<CodexUsagePollResult> pollTask = poller.PollAsync(
				accountId,
				CancellationToken.None);
			FakeTransport transport = await transportCreated.Task.WaitAsync(
				TimeSpan.FromSeconds(5));
			await Assert.ThrowsAsync<TimeoutException>(() => pollTask)
				.WaitAsync(TimeSpan.FromSeconds(5));
			await transport.AbortObserved.WaitAsync(TimeSpan.FromSeconds(5));
			await transport.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(releaseDispose.Task.IsCompleted);

			await Assert.ThrowsAsync<TimeoutException>(() =>
				poller.PollAsync(accountId, CancellationToken.None))
				.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(1, Volatile.Read(ref factoryCallCount));
		}
		finally
		{
			releaseDispose.TrySetResult();
		}

		using CancellationTokenSource gateReleaseTimeout = new(
			TimeSpan.FromSeconds(5));
		using IDisposable releasedLease = await operationGate.EnterAsync(
			accountId,
			gateReleaseTimeout.Token);
	}

	[Fact]
	public async Task PollAsync_WhenAccountContainmentWasCompromised_ThrowsWithoutStartingTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		CodexAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		ProviderProcessOperationTracker operationTracker = new(
			() => operationGate.MarkContainmentCompromised(accountId));
		IDisposable rawLease = await operationGate.EnterAsync(
			accountId,
			CancellationToken.None);
		using IDisposable trackedLease = operationTracker.HoldLease(rawLease);
		operationTracker.MarkContainmentCompromised();
		trackedLease.Dispose();
		int transportFactoryCallCount = 0;
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => throw new InvalidOperationException(
				"executable resolver must not run"),
			startInfo =>
			{
				Interlocked.Increment(ref transportFactoryCallCount);
				return new FakeTransport(startInfo);
			},
			new FakeTimeProvider(ObservedAt),
			operationGate);

		CodexCliContainmentException exception =
			await Assert.ThrowsAsync<CodexCliContainmentException>(() =>
				poller.PollAsync(accountId));

		Assert.Contains("重新啟動", exception.Message, StringComparison.Ordinal);
		Assert.Equal(0, Volatile.Read(ref transportFactoryCallCount));
	}

	[Fact]
	public async Task PollAsync_WhenContainmentChangesWhileWaitingForGate_ThrowsContainmentInsteadOfTimeout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		CodexAccountOperationGate operationGate = new();
		Guid accountId = Guid.NewGuid();
		int transportFactoryCallCount = 0;
		using IDisposable heldLease = await operationGate.EnterAsync(
			accountId,
			CancellationToken.None);
		CodexAppServerUsagePoller poller = new(
			_ => temporaryDirectory.Path,
			() => executablePath,
			startInfo =>
			{
				Interlocked.Increment(ref transportFactoryCallCount);
				return new FakeTransport(startInfo);
			},
			new FakeTimeProvider(ObservedAt),
			operationGate,
			TimeSpan.FromMilliseconds(250));
		Task<CodexUsagePollResult> pollTask = poller.PollAsync(
			accountId,
			CancellationToken.None);

		await Task.Delay(TimeSpan.FromMilliseconds(50));
		operationGate.MarkContainmentCompromised(accountId);

		CodexCliContainmentException exception =
			await Assert.ThrowsAsync<CodexCliContainmentException>(() => pollTask)
				.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Contains("重新啟動", exception.Message, StringComparison.Ordinal);
		Assert.Equal(0, Volatile.Read(ref transportFactoryCallCount));
	}

	private static CodexAppServerUsagePoller CreatePoller(
		string homeDirectory,
		string executablePath,
		IEnumerable<string> responses)
	{
		return new CodexAppServerUsagePoller(
			_ => homeDirectory,
			() => executablePath,
			startInfo => new FakeTransport(startInfo, responses),
			new FakeTimeProvider(ObservedAt));
	}

	private static CodexAppServerUsagePoller CreateBoundPoller(
		string homeDirectory,
		string executablePath,
		IEnumerable<string> responses,
		ICodexWorkspaceBindingStore bindingStore,
		Func<ProcessStartInfo, ICodexAppServerTransport>? transportFactory = null)
	{
		return new CodexAppServerUsagePoller(
			_ => homeDirectory,
			() => executablePath,
			transportFactory ??
				(startInfo => new FakeTransport(startInfo, responses)),
			new FakeTimeProvider(ObservedAt),
			new CodexAccountOperationGate(),
			bindingStore,
			new CodexBindingCommitGate());
	}

	private static string CreateConfigReadResponse(object workspaceValue)
	{
		return JsonSerializer.Serialize(new
		{
			id = 6,
			result = new
			{
				config = new Dictionary<string, object>
				{
					["forced_chatgpt_workspace_id"] = workspaceValue
				},
				origins = new { }
			}
		});
	}

	private static string[] CreateResponses(
		string legacyRateLimits,
		string multiBucketRateLimits,
		string resetCredits = "null")
	{
		return new[]
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			$"{{\"id\":3,\"result\":{{\"rateLimits\":{legacyRateLimits},\"rateLimitsByLimitId\":{multiBucketRateLimits},\"rateLimitResetCredits\":{resetCredits}}}}}"
		};
	}

	private static object CreateRateLimitBucketPayload(
		string limitId,
		string? limitName = null,
		bool includeSecondary = false)
	{
		object? secondary = includeSecondary
			? new
			{
				usedPercent = 20,
				windowDurationMins = 10080,
				resetsAt = 1784704800
			}
			: null;

		return new
		{
			limitId,
			limitName,
			primary = new
			{
				usedPercent = 10,
				windowDurationMins = 300,
				resetsAt = 1784100000
			},
			secondary
		};
	}

	private static string CreateExecutable(string directory)
	{
		string executablePath = Path.Combine(directory, "codex.exe");
		File.WriteAllBytes(executablePath, Array.Empty<byte>());
		return executablePath;
	}

	private static async Task WaitUntilAsync(
		Func<bool> condition,
		TimeSpan timeout)
	{
		long startedAt = Stopwatch.GetTimestamp();

		while (!condition())
		{
			if (Stopwatch.GetElapsedTime(startedAt) >= timeout)
			{
				throw new TimeoutException("The expected condition was not reached.");
			}

			await Task.Delay(TimeSpan.FromMilliseconds(10));
		}
	}

	private static string GetMethod(string message)
	{
		using JsonDocument document = JsonDocument.Parse(message);
		return document.RootElement.GetProperty("method").GetString()!;
	}

	private static bool GetRefreshToken(string message)
	{
		using JsonDocument document = JsonDocument.Parse(message);
		return document.RootElement
			.GetProperty("params")
			.GetProperty("refreshToken")
			.GetBoolean();
	}
}
