using System.Diagnostics;
using System.Text.Json;

using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CodexAccountLoginTests
{
	private static readonly Guid WorkspaceId =
		Guid.Parse("7f70e920-d4dc-4ba5-8dbc-2d3961c7336a");

	private sealed class FakeTransport : ICodexAppServerTransport
	{
		private readonly Queue<string> _automaticResponses = new();
		private readonly bool _automaticWorkspaceProtocol;
		private readonly bool _blockWhenResponsesAreExhausted;
		private readonly Action? _disposeAction;
		private readonly bool _suppressConfigWriteResponse;
		private readonly Queue<object?> _configuredWorkspaceValues;
		private readonly Task? _disposeBlock;
		private readonly Queue<string> _responses;

		public ProcessStartInfo StartInfo { get; }

		internal bool IsAborted { get; private set; }

		internal bool IsDisposed { get; private set; }

		internal TaskCompletionSource DisposeStarted { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource ConfigWriteStarted { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);

		internal List<string> WrittenMessages { get; } = new();

		internal FakeTransport(
			ProcessStartInfo startInfo,
			IEnumerable<string> responses,
			bool blockWhenResponsesAreExhausted = false,
			Task? disposeBlock = null,
			IEnumerable<object?>? configuredWorkspaceValues = null,
			bool automaticWorkspaceProtocol = true,
			bool suppressConfigWriteResponse = false,
			Action? disposeAction = null)
		{
			StartInfo = startInfo;
			_responses = new Queue<string>(responses);
			_blockWhenResponsesAreExhausted = blockWhenResponsesAreExhausted;
			_disposeBlock = disposeBlock;
			_disposeAction = disposeAction;
			_automaticWorkspaceProtocol = automaticWorkspaceProtocol;
			_suppressConfigWriteResponse = suppressConfigWriteResponse;
			_configuredWorkspaceValues = new Queue<object?>(
				configuredWorkspaceValues ??
				[
					WorkspaceId.ToString("D"),
					WorkspaceId.ToString("D")
				]);
		}

		public void Abort()
		{
			IsAborted = true;
		}

		public async ValueTask<string?> ReadLineAsync(
			CancellationToken cancellationToken)
		{
			if ((_automaticResponses.Count == 0) &&
				(_responses.Count == 0) &&
				_blockWhenResponsesAreExhausted)
			{
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
			QueueWorkspaceProtocolResponse(message);
			return ValueTask.CompletedTask;
		}

		private void QueueWorkspaceProtocolResponse(string message)
		{
			if (!_automaticWorkspaceProtocol)
			{
				return;
			}

			using JsonDocument document = JsonDocument.Parse(message);
			JsonElement root = document.RootElement;
			string? method = root.GetProperty("method").GetString();

			if (string.Equals(
					method,
					"config/value/write",
					StringComparison.Ordinal))
			{
				ConfigWriteStarted.TrySetResult();
				if (_suppressConfigWriteResponse)
				{
					return;
				}

				_automaticResponses.Enqueue(JsonSerializer.Serialize(new
				{
					id = root.GetProperty("id").GetInt32(),
					result = new
					{
						status = "ok",
						version = "test-version",
						filePath = Path.Combine(
							StartInfo.WorkingDirectory,
							"config.toml")
					}
				}));
			}
			else if (string.Equals(method, "config/read", StringComparison.Ordinal))
			{
				object? workspaceValue = _configuredWorkspaceValues.Count == 0
					? null
					: _configuredWorkspaceValues.Dequeue();
				_automaticResponses.Enqueue(JsonSerializer.Serialize(new
				{
					id = root.GetProperty("id").GetInt32(),
					result = new
					{
						config = new Dictionary<string, object?>
						{
							["forced_chatgpt_workspace_id"] = workspaceValue
						},
						origins = new { }
					}
				}));
			}
		}

		public async ValueTask DisposeAsync()
		{
			IsDisposed = true;
			DisposeStarted.TrySetResult();
			_disposeAction?.Invoke();

			if (_disposeBlock is not null)
			{
				await _disposeBlock;
			}
		}
	}

	[Fact]
	public async Task StartAsync_WhenLoginCompletes_UsesOfficialFlowAndCreatesIsolatedHome()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string homeDirectory = Path.Combine(temporaryDirectory.Path, "codex-home");
		List<FakeTransport> transports = new();
		CodexAccountLogin login = CreateLogin(
			homeDirectory,
			executablePath,
			startInfo =>
			{
				FakeTransport transport = new(
					startInfo,
					CreateSuccessfulResponses());
				transports.Add(transport);
				return transport;
			});

		await using (ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None))
		{
			Assert.Equal(
				new Uri("https://auth.openai.com/codex/login?state=test"),
				session.AuthorizationUri);
			CodexAccountLoginResult result =
				await session.WaitForCompletionAsync(CancellationToken.None);
			await using ICodexWorkspaceConfigurationTransaction transaction =
				session.TakeWorkspaceConfigurationTransaction();
			transaction.Commit();
			Assert.Equal("user@example.com", result.AccountIdentity);
			Assert.Equal(WorkspaceId, result.WorkspaceId);
		}

		Assert.Equal(2, transports.Count);
		FakeTransport writerTransport = transports[0];
		FakeTransport loginTransport = transports[1];
		Assert.True(Directory.Exists(homeDirectory));
		Assert.False(writerTransport.IsAborted);
		Assert.True(writerTransport.IsDisposed);
		Assert.False(loginTransport.IsAborted);
		Assert.True(loginTransport.IsDisposed);
		Assert.Equal(homeDirectory, loginTransport.StartInfo.Environment["CODEX_HOME"]);
		Assert.Equal(homeDirectory,
			loginTransport.StartInfo.Environment["CODEX_SQLITE_HOME"]);
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"config/value/write"
			},
			writerTransport.WrittenMessages.Select(GetMethod));
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"account/login/start",
				"account/read",
				"config/read"
			},
			loginTransport.WrittenMessages.Select(GetMethod));
		using JsonDocument writeRequest = JsonDocument.Parse(
			writerTransport.WrittenMessages[3]);
		Assert.Equal(
			"forced_chatgpt_workspace_id",
			writeRequest.RootElement
				.GetProperty("params")
				.GetProperty("keyPath")
				.GetString());
		Assert.Equal(
			WorkspaceId.ToString("D"),
			writeRequest.RootElement
				.GetProperty("params")
				.GetProperty("value")
				.GetString());
		Assert.False(writeRequest.RootElement
			.GetProperty("params")
			.TryGetProperty("filePath", out _));
		Assert.False(writeRequest.RootElement
			.GetProperty("params")
			.TryGetProperty("expectedVersion", out _));
		using JsonDocument preReadRequest = JsonDocument.Parse(
			loginTransport.WrittenMessages[2]);
		Assert.False(preReadRequest.RootElement
			.GetProperty("params")
			.GetProperty("includeLayers")
			.GetBoolean());
		using JsonDocument postReadRequest = JsonDocument.Parse(
			loginTransport.WrittenMessages[5]);
		Assert.False(postReadRequest.RootElement
			.GetProperty("params")
			.GetProperty("includeLayers")
			.GetBoolean());
		using JsonDocument loginRequest = JsonDocument.Parse(
			loginTransport.WrittenMessages[3]);
		Assert.Equal(
			"chatgpt",
			loginRequest.RootElement
				.GetProperty("params")
				.GetProperty("type")
				.GetString());
	}

	[Fact]
	public async Task StartAsync_WithoutWorkspace_ClearsForcedWorkspaceBeforeAccountLevelLogin()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string homeDirectory = Path.Combine(temporaryDirectory.Path, "codex-home");
		List<FakeTransport> transports = new();
		int transportIndex = 0;
		CodexAccountLogin login = CreateLogin(
			homeDirectory,
			executablePath,
			startInfo =>
			{
				int currentTransportIndex = transportIndex++;
				FakeTransport transport = new(
					startInfo,
					CreateSuccessfulResponses(),
					configuredWorkspaceValues: currentTransportIndex == 0
						? [WorkspaceId.ToString("D")]
						: [null, null]);
				transports.Add(transport);
				return transport;
			});

		await using (ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			CancellationToken.None))
		{
			CodexAccountLoginResult result =
				await session.WaitForCompletionAsync(CancellationToken.None);
			await using ICodexWorkspaceConfigurationTransaction transaction =
				session.TakeWorkspaceConfigurationTransaction();
			transaction.Commit();
			Assert.Equal("user@example.com", result.AccountIdentity);
			Assert.Null(result.WorkspaceId);
		}

		Assert.Equal(2, transports.Count);
		FakeTransport writerTransport = transports[0];
		FakeTransport loginTransport = transports[1];
		Assert.True(writerTransport.IsDisposed);
		Assert.True(loginTransport.IsDisposed);
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"config/value/write"
			},
			writerTransport.WrittenMessages.Select(GetMethod));
		Assert.Equal(
			new[]
			{
				"initialize",
				"initialized",
				"config/read",
				"account/login/start",
				"account/read",
				"config/read"
			},
			loginTransport.WrittenMessages.Select(GetMethod));
		using JsonDocument clearRequest = JsonDocument.Parse(
			writerTransport.WrittenMessages[3]);
		Assert.Equal(
			JsonValueKind.Null,
			clearRequest.RootElement
				.GetProperty("params")
				.GetProperty("value")
				.ValueKind);
	}

	[Fact]
	public async Task DisposeAsync_WhenWorkspaceLoginIsCancelled_RestoresPreviousWorkspace()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid previousWorkspaceId = Guid.NewGuid();
		List<FakeTransport> transports = new();
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo =>
			{
				int transportIndex = transports.Count;
				FakeTransport transport = new(
					startInfo,
					CreateSuccessfulResponses(),
					configuredWorkspaceValues: transportIndex switch
					{
						0 => [previousWorkspaceId.ToString("D")],
						1 => [WorkspaceId.ToString("D")],
						3 => [previousWorkspaceId.ToString("D")],
						_ => []
					});
				transports.Add(transport);
				return transport;
			});

		ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None);
		await session.DisposeAsync();

		Assert.Equal(4, transports.Count);
		using JsonDocument restoreRequest = JsonDocument.Parse(
			transports[2].WrittenMessages.Single(message =>
				string.Equals(
					GetMethod(message),
					"config/value/write",
					StringComparison.Ordinal)));
		Assert.Equal(
			previousWorkspaceId.ToString("D"),
			restoreRequest.RootElement
				.GetProperty("params")
				.GetProperty("value")
				.GetString());
		Assert.Equal(
			new[] { "initialize", "initialized", "config/read" },
			transports[3].WrittenMessages.Select(GetMethod));
	}

	[Fact]
	public async Task DisposeAsync_WhenAccountLevelLoginIsCancelled_RestoresPreviousWorkspace()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid previousWorkspaceId = Guid.NewGuid();
		List<FakeTransport> transports = new();
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo =>
			{
				int transportIndex = transports.Count;
				FakeTransport transport = new(
					startInfo,
					CreateSuccessfulResponses(),
					configuredWorkspaceValues: transportIndex switch
					{
						0 => [previousWorkspaceId.ToString("D")],
						1 => [null],
						3 => [previousWorkspaceId.ToString("D")],
						_ => []
					});
				transports.Add(transport);
				return transport;
			});

		ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			CancellationToken.None);
		await session.DisposeAsync();

		Assert.Equal(4, transports.Count);
		using JsonDocument restoreRequest = JsonDocument.Parse(
			transports[2].WrittenMessages.Single(message =>
				string.Equals(
					GetMethod(message),
					"config/value/write",
					StringComparison.Ordinal)));
		Assert.Equal(
			previousWorkspaceId.ToString("D"),
			restoreRequest.RootElement
				.GetProperty("params")
				.GetProperty("value")
				.GetString());
	}

	[Fact]
	public async Task WorkspaceConfigurationTransaction_WhenBindingCommitFails_RestoresAfterSessionDisposal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid previousWorkspaceId = Guid.NewGuid();
		List<FakeTransport> transports = new();
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo =>
			{
				int transportIndex = transports.Count;
				FakeTransport transport = new(
					startInfo,
					CreateSuccessfulResponses(),
					configuredWorkspaceValues: transportIndex switch
					{
						0 => [previousWorkspaceId.ToString("D")],
						1 =>
						[
							WorkspaceId.ToString("D"),
							WorkspaceId.ToString("D")
						],
						3 => [previousWorkspaceId.ToString("D")],
						_ => []
					});
				transports.Add(transport);
				return transport;
			});

		ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None);
		await session.WaitForCompletionAsync(CancellationToken.None);
		ICodexWorkspaceConfigurationTransaction transaction =
			session.TakeWorkspaceConfigurationTransaction();

		await session.DisposeAsync();
		Assert.Equal(2, transports.Count);

		await transaction.DisposeAsync();

		Assert.Equal(4, transports.Count);
		using JsonDocument restoreRequest = JsonDocument.Parse(
			transports[2].WrittenMessages.Single(message =>
				string.Equals(
					GetMethod(message),
					"config/value/write",
					StringComparison.Ordinal)));
		Assert.Equal(
			previousWorkspaceId.ToString("D"),
			restoreRequest.RootElement
				.GetProperty("params")
				.GetProperty("value")
				.GetString());
	}

	[Fact]
	public async Task StartAsync_WhenWorkspaceIsSingleElementArray_AcceptsSemanticUuid()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo => new FakeTransport(
				startInfo,
				CreateSuccessfulResponses(),
				configuredWorkspaceValues:
				[
					new[] { WorkspaceId.ToString("B") },
					new[] { WorkspaceId.ToString("N") }
				]));

		await using ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None);
		CodexAccountLoginResult result =
			await session.WaitForCompletionAsync(CancellationToken.None);
		await using ICodexWorkspaceConfigurationTransaction transaction =
			session.TakeWorkspaceConfigurationTransaction();
		transaction.Commit();

		Assert.Equal(WorkspaceId, result.WorkspaceId);
	}

	[Fact]
	public async Task StartAsync_WhenConfigWriteTargetsDifferentFile_FailsBeforeLoginProcess()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		int transportFactoryCallCount = 0;
		FakeTransport? firstTransport = null;
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo =>
			{
				int transportIndex =
					Interlocked.Increment(ref transportFactoryCallCount) - 1;
				if (transportIndex != 0)
				{
					return new FakeTransport(
						startInfo,
						CreateSuccessfulResponses());
				}

				firstTransport = new FakeTransport(
					startInfo,
					[
						"""{"id":1,"result":{}}""",
						JsonSerializer.Serialize(new
						{
							id = 7,
							result = new
							{
								config = new Dictionary<string, object?>
								{
									["forced_chatgpt_workspace_id"] =
										WorkspaceId.ToString("D")
								},
								origins = new { }
							}
						}),
						JsonSerializer.Serialize(new
						{
							id = 8,
							result = new
							{
								status = "ok",
								version = "test-version",
								filePath = Path.Combine(
									temporaryDirectory.Path,
									"other-config.toml")
							}
						})
					],
					automaticWorkspaceProtocol: false);
				return firstTransport;
			});

		await Assert.ThrowsAsync<CodexWorkspaceConfigurationException>(() =>
			login.StartAsync(Guid.NewGuid(), WorkspaceId));

		Assert.Equal(3, Volatile.Read(ref transportFactoryCallCount));
		Assert.NotNull(firstTransport);
		Assert.True(firstTransport.IsAborted);
		Assert.True(firstTransport.IsDisposed);
	}

	[Fact]
	public async Task StartAsync_WhenCancelledAfterConfigWrite_ConfirmsWriterCleanupBeforeRestore()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid previousWorkspaceId = Guid.NewGuid();
		List<FakeTransport> transports = new();
		TaskCompletionSource<FakeTransport> writerCreated = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		CodexAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "codex-home"),
			() => executablePath,
			startInfo =>
			{
				int transportIndex = transports.Count;
				if (transportIndex > 0)
				{
					Assert.True(transports[0].IsAborted);
					Assert.True(transports[0].IsDisposed);
				}

				FakeTransport transport = new(
					startInfo,
					transportIndex == 0
						? ["""{"id":1,"result":{}}"""]
						: CreateSuccessfulResponses(),
					blockWhenResponsesAreExhausted: transportIndex == 0,
					configuredWorkspaceValues: transportIndex switch
					{
						0 or 2 => [previousWorkspaceId.ToString("D")],
						_ => []
					},
					suppressConfigWriteResponse: transportIndex == 0);
				transports.Add(transport);
				if (transportIndex == 0)
				{
					writerCreated.TrySetResult(transport);
				}
				return transport;
			},
			new CodexAccountOperationGate(),
			TimeSpan.FromSeconds(5));
		using CancellationTokenSource cancellationSource = new();
		Task<ICodexAccountLoginSession> startTask = login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			cancellationSource.Token);
		FakeTransport writer = await writerCreated.Task.WaitAsync(
			TimeSpan.FromSeconds(5));
		await writer.ConfigWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		cancellationSource.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
		Assert.True(writer.IsAborted);
		Assert.Equal(3, transports.Count);
		Assert.Contains(
			transports[1].WrittenMessages,
			message => string.Equals(
				GetMethod(message),
				"config/value/write",
				StringComparison.Ordinal));
	}

	[Fact]
	public async Task StartAsync_WhenWorkspaceArrayContainsMultipleValues_FailsBeforeLogin()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid otherWorkspaceId = Guid.NewGuid();
		List<FakeTransport> transports = new();
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo =>
			{
				int transportIndex = transports.Count;
				FakeTransport transport = new(
					startInfo,
					CreateSuccessfulResponses(),
					configuredWorkspaceValues: transportIndex switch
					{
						0 or 1 or 3 =>
						[
							new[]
							{
								WorkspaceId.ToString("D"),
								otherWorkspaceId.ToString("D")
							}
						],
						_ => []
					});
				transports.Add(transport);
				return transport;
			});

		await Assert.ThrowsAsync<CodexWorkspaceConfigurationException>(() =>
			login.StartAsync(Guid.NewGuid(), WorkspaceId));

		Assert.Equal(4, transports.Count);
		FakeTransport loginTransport = transports[1];
		Assert.DoesNotContain(
			loginTransport.WrittenMessages,
			message => string.Equals(
				GetMethod(message),
				"account/login/start",
				StringComparison.Ordinal));
		Assert.True(loginTransport.IsAborted);
	}

	[Fact]
	public async Task StartAsync_WhenConfiguredWorkspaceDiffers_FailsBeforeLogin()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid previousWorkspaceId = Guid.NewGuid();
		Guid mismatchedWorkspaceId = Guid.NewGuid();
		int transportIndex = 0;
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo =>
			{
				int currentTransportIndex = transportIndex++;
				return new FakeTransport(
					startInfo,
					CreateSuccessfulResponses(),
					configuredWorkspaceValues: currentTransportIndex switch
					{
						0 or 3 => [previousWorkspaceId.ToString("D")],
						1 => [mismatchedWorkspaceId.ToString("D")],
						_ => []
					});
			});

		await Assert.ThrowsAsync<CodexWorkspaceMismatchException>(() =>
			login.StartAsync(Guid.NewGuid(), WorkspaceId));
	}

	[Fact]
	public async Task WaitForCompletionAsync_WhenWorkspaceChangesAfterLogin_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo => new FakeTransport(
				startInfo,
				CreateSuccessfulResponses(),
				configuredWorkspaceValues:
				[
					WorkspaceId.ToString("D"),
					Guid.NewGuid().ToString("D")
				]));

		await using ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None);

		await Assert.ThrowsAsync<CodexWorkspaceMismatchException>(() =>
			session.WaitForCompletionAsync(CancellationToken.None));
	}

	[Fact]
	public async Task WaitForCompletionAsync_WhenCompletionLoginIdIsMissing_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo => new FakeTransport(
				startInfo,
				[
					"""{"id":1,"result":{}}""",
					"""{"id":10,"result":{"type":"chatgpt","loginId":"login-1","authUrl":"https://auth.openai.com/codex/login"}}""",
					"""{"method":"account/login/completed","params":{"success":true,"error":null}}"""
				]));

		await using ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None);

		await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
			session.WaitForCompletionAsync(CancellationToken.None));
	}

	[Fact]
	public async Task StartAsync_WhenCliIsUntrusted_ExplainsOfficialRecovery()
	{
		using TemporaryDirectory temporaryDirectory = new();
		int transportFactoryCallCount = 0;
		CodexAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "codex-home"),
			() => throw new CodexCliUntrustedException(
				"Untrusted resolver detail."),
			startInfo =>
			{
				transportFactoryCallCount++;
				return new FakeTransport(startInfo, []);
			});

		CodexAccountLoginException exception =
			await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
				login.StartAsync(Guid.NewGuid(), WorkspaceId));

		Assert.Contains("OpenAI 官方來源", exception.Message);
		Assert.DoesNotContain("請再試一次", exception.Message);
		Assert.IsType<CodexCliUntrustedException>(exception.InnerException);
		Assert.Equal(0, transportFactoryCallCount);
	}

	[Fact]
	public async Task StartAsync_WhenVersionProbeFails_AsksUserToRetryLater()
	{
		using TemporaryDirectory temporaryDirectory = new();
		int transportFactoryCallCount = 0;
		CodexCliProbeException probeException = new(
			"Probe failed.",
			new TimeoutException("Probe timed out."));
		CodexAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "codex-home"),
			() => throw probeException,
			startInfo =>
			{
				transportFactoryCallCount++;
				return new FakeTransport(startInfo, []);
			});

		CodexAccountLoginException exception =
			await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
				login.StartAsync(Guid.NewGuid(), WorkspaceId));

		Assert.Contains("請稍後再試", exception.Message);
		Assert.DoesNotContain("重新安裝", exception.Message);
		Assert.Same(probeException, exception.InnerException);
		Assert.Equal(0, transportFactoryCallCount);
	}

	[Fact]
	public async Task StartAsync_WhenContainmentFails_AsksUserToRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		int transportFactoryCallCount = 0;
		CodexCliContainmentException containmentException = new(
			"Containment failed.",
			new InvalidOperationException("Job query failed."));
		CodexAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "codex-home"),
			() => throw containmentException,
			startInfo =>
			{
				transportFactoryCallCount++;
				return new FakeTransport(startInfo, []);
			});

		CodexAccountLoginException exception =
			await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
				login.StartAsync(Guid.NewGuid(), WorkspaceId));

		Assert.Contains("重新啟動", exception.Message);
		Assert.DoesNotContain("請稍後再試", exception.Message);
		Assert.Same(containmentException, exception.InnerException);
		Assert.Equal(0, transportFactoryCallCount);
	}

	[Fact]
	public async Task StartAsync_WhenAccountContainmentWasCompromised_RequiresRestartWithoutStartingTransport()
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
		CodexAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "codex-home"),
			() => throw new InvalidOperationException(
				"executable resolver must not run"),
			startInfo =>
			{
				Interlocked.Increment(ref transportFactoryCallCount);
				return new FakeTransport(startInfo, []);
			},
			operationGate);

		CodexAccountLoginException exception =
			await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
				login.StartAsync(accountId, WorkspaceId));

		Assert.Contains("重新啟動", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("稍後再試", exception.Message, StringComparison.Ordinal);
		Assert.IsType<CodexCliContainmentException>(exception.InnerException);
		Assert.Equal(0, Volatile.Read(ref transportFactoryCallCount));
	}

	[Fact]
	public async Task DisposeAsync_WhenCompletedSessionCleanupCompromisesContainment_BlocksCommit()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string homeDirectory = Path.Combine(temporaryDirectory.Path, "codex-home");
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		List<FakeTransport> transports = new();
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		CodexAccountLogin login = CreateContainmentCompromisingLogin(
			homeDirectory,
			executablePath,
			executableLease,
			operationGate,
			transports);
		ICodexWorkspaceConfigurationTransaction? transaction = null;
		ICodexAccountLoginSession? session = null;

		try
		{
			session = await login.StartAsync(
				accountId,
				WorkspaceId,
				CancellationToken.None);
			bool reachedDurableCommit = false;
			CodexAccountLoginException cleanupException =
				await Assert.ThrowsAsync<CodexAccountLoginException>(async () =>
				{
					await using (session)
					{
						await session.WaitForCompletionAsync(
							CancellationToken.None);
						transaction =
							session.TakeWorkspaceConfigurationTransaction();
					}

					reachedDurableCommit = true;
				});

			Assert.Contains(
				"重新啟動",
				cleanupException.Message,
				StringComparison.Ordinal);
			Assert.False(reachedDurableCommit);
			Assert.Equal(2, transports.Count);
			Assert.NotNull(transaction);
			CodexAccountLoginException commitException = Assert.Throws<
				CodexAccountLoginException>(() => transaction.Commit());
			Assert.Contains(
				"重新啟動",
				commitException.Message,
				StringComparison.Ordinal);
			CodexAccountLoginException rollbackException =
				await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
					transaction.DisposeAsync().AsTask());
			Assert.Contains(
				"重新啟動",
				rollbackException.Message,
				StringComparison.Ordinal);
			Assert.Equal(2, transports.Count);
			Assert.True(executableLease.IsProtected);
			await Assert.ThrowsAsync<CodexCliContainmentException>(() =>
				operationGate.EnterAsync(
					accountId,
					CancellationToken.None).AsTask());
		}
		finally
		{
			if (transaction is not null)
			{
				try
				{
					await transaction.DisposeAsync();
				}
				catch (CodexAccountLoginException)
				{
				}
			}

			if (session is not null)
			{
				try
				{
					await session.DisposeAsync();
				}
				catch (CodexAccountLoginException)
				{
				}
			}

			executableLease.Dispose();
		}
	}

	[Fact]
	public async Task DisposeAsync_WhenCancelledSessionCleanupCompromisesContainment_SkipsRestore()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string homeDirectory = Path.Combine(temporaryDirectory.Path, "codex-home");
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		List<FakeTransport> transports = new();
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		CodexAccountLogin login = CreateContainmentCompromisingLogin(
			homeDirectory,
			executablePath,
			executableLease,
			operationGate,
			transports);
		ICodexAccountLoginSession? session = null;

		try
		{
			session = await login.StartAsync(
				accountId,
				WorkspaceId,
				CancellationToken.None);
			CodexAccountLoginException exception =
				await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
					session.DisposeAsync().AsTask());

			Assert.Contains(
				"重新啟動",
				exception.Message,
				StringComparison.Ordinal);
			Assert.Equal(2, transports.Count);
			Assert.True(transports[1].IsAborted);
			Assert.True(executableLease.IsProtected);
			await Assert.ThrowsAsync<CodexCliContainmentException>(() =>
				operationGate.EnterAsync(
					accountId,
					CancellationToken.None).AsTask());
		}
		finally
		{
			if (session is not null)
			{
				try
				{
					await session.DisposeAsync();
				}
				catch (CodexAccountLoginException)
				{
				}
			}

			executableLease.Dispose();
		}
	}

	[Fact]
	public async Task WaitForCompletionAsync_WhenAccountIdentityIsInitiallyUnavailable_RetriesWithoutSecondLogin()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeTransport? transport = null;
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo => transport = new FakeTransport(
				startInfo,
				new[]
				{
					"""{"id":1,"result":{}}""",
					"""{"id":10,"result":{"type":"chatgpt","loginId":"login-1","authUrl":"https://auth.openai.com/codex/login"}}""",
					"""{"method":"account/login/completed","params":{"loginId":"login-1","success":true,"error":null}}""",
					"""{"id":11,"result":{"account":null,"requiresOpenaiAuth":true}}""",
					"""{"id":12,"result":{"account":{"type":"chatgpt","email":"retry@example.com","planType":"plus"},"requiresOpenaiAuth":true}}"""
				}));

		await using ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None);
		CodexAccountLoginResult result =
			await session.WaitForCompletionAsync(CancellationToken.None);
		await using ICodexWorkspaceConfigurationTransaction transaction =
			session.TakeWorkspaceConfigurationTransaction();
		transaction.Commit();

		Assert.Equal("retry@example.com", result.AccountIdentity);
		Assert.NotNull(transport);
		Assert.Equal(
			1,
			transport.WrittenMessages.Count(message =>
				string.Equals(
					GetMethod(message),
					"account/login/start",
					StringComparison.Ordinal)));
		Assert.Equal(
			2,
			transport.WrittenMessages.Count(message =>
				string.Equals(
					GetMethod(message),
					"account/read",
					StringComparison.Ordinal)));
		Assert.False(transport.IsAborted);
	}

	[Fact]
	public async Task WaitForCompletionAsync_WhenChatGptAccountHasNoStableIdentity_StopsRetryingAndExplainsAction()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeTransport? transport = null;
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo => transport = new FakeTransport(
				startInfo,
				new[]
				{
					"""{"id":1,"result":{}}""",
					"""{"id":10,"result":{"type":"chatgpt","loginId":"login-1","authUrl":"https://auth.openai.com/codex/login"}}""",
					"""{"method":"account/login/completed","params":{"loginId":"login-1","success":true,"error":null}}""",
					"""{"id":11,"result":{"account":{"type":"chatgpt","email":null,"planType":"enterprise"},"requiresOpenaiAuth":true}}"""
				}));

		await using ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None);
		CodexAccountLoginException exception =
			await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
				session.WaitForCompletionAsync(CancellationToken.None));

		Assert.Contains("無法確認目前登入的帳號", exception.Message);
		Assert.Contains("其他 ChatGPT 帳號", exception.Message);
		Assert.NotNull(transport);
		Assert.Equal(
			1,
			transport.WrittenMessages.Count(message =>
				string.Equals(
					GetMethod(message),
					"account/read",
					StringComparison.Ordinal)));
		Assert.True(transport.IsAborted);
	}

	[Fact]
	public async Task StartAsync_WhenAuthorizationUrlIsNotHttps_AbortsAndDisposesTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		List<FakeTransport> transports = new();
		CodexAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "codex-home"),
			() => executablePath,
			startInfo =>
			{
				FakeTransport transport = new(
					startInfo,
					new[]
					{
						"""{"id":1,"result":{}}""",
						"""{"id":10,"result":{"type":"chatgpt","loginId":"login-1","authUrl":"http://auth.openai.com/codex/login"}}"""
					});
				transports.Add(transport);
				return transport;
			},
			operationGate);

		await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
			login.StartAsync(accountId, WorkspaceId, CancellationToken.None));

		Assert.Equal(4, transports.Count);
		Assert.True(transports[1].IsAborted);
		Assert.True(transports[1].IsDisposed);
		using CancellationTokenSource cancellationSource = new(
			TimeSpan.FromSeconds(1));
		using IDisposable lease = await operationGate.EnterAsync(
			accountId,
			cancellationSource.Token);
	}

	[Fact]
	public async Task StartAsync_WhenTimeoutCleanupBlocks_RetainsAccountGate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		TaskCompletionSource releaseDispose = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<FakeTransport> transportCreated = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		int factoryCallCount = 0;
		CodexAccountLogin login = new(
			_ => Path.Combine(temporaryDirectory.Path, "codex-home"),
			() => executablePath,
			startInfo =>
			{
				Interlocked.Increment(ref factoryCallCount);
				FakeTransport transport = new(
					startInfo,
					Array.Empty<string>(),
					blockWhenResponsesAreExhausted: true,
					disposeBlock: releaseDispose.Task);
				transportCreated.TrySetResult(transport);
				return transport;
			},
			operationGate,
			TimeSpan.FromMilliseconds(250));

		try
		{
			Task<ICodexAccountLoginSession> loginTask = login.StartAsync(
				accountId,
				WorkspaceId,
				CancellationToken.None);
			FakeTransport transport = await transportCreated.Task.WaitAsync(
				TimeSpan.FromSeconds(5));
			await Assert.ThrowsAsync<CodexAccountLoginException>(() => loginTask)
				.WaitAsync(TimeSpan.FromSeconds(5));
			await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

			CodexAccountLoginException blockedException =
				await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
					login.StartAsync(
						accountId,
						WorkspaceId,
						CancellationToken.None))
					.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Contains(
				"等待前一個",
				blockedException.Message,
				StringComparison.Ordinal);
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
	public async Task SharedOperationGate_PollWaitsUntilLoginSessionIsDisposed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		string homeDirectory = Path.Combine(temporaryDirectory.Path, "codex-home");
		Guid accountId = Guid.NewGuid();
		CodexAccountOperationGate operationGate = new();
		int loginTransportIndex = 0;
		CodexAccountLogin login = new(
			_ => homeDirectory,
			() => executablePath,
			startInfo =>
			{
				int currentTransportIndex = loginTransportIndex++;
				return new FakeTransport(
					startInfo,
					CreateSuccessfulResponses(),
					configuredWorkspaceValues: currentTransportIndex == 0
						? [null]
						: [null, null]);
			},
			operationGate);
		bool pollTransportStarted = false;
		CodexAppServerUsagePoller poller = new(
			_ => homeDirectory,
			() => executablePath,
			startInfo =>
			{
				pollTransportStarted = true;
				return new FakeTransport(
					startInfo,
					CreateSuccessfulPollResponses(),
					configuredWorkspaceValues: [null]);
			},
			TimeProvider.System,
			operationGate);
		ICodexAccountLoginSession session = await login.StartAsync(
			accountId,
			CancellationToken.None);
		Task<CodexUsagePollResult>? pollTask = null;

		try
		{
			await session.WaitForCompletionAsync(CancellationToken.None);
			await using ICodexWorkspaceConfigurationTransaction transaction =
				session.TakeWorkspaceConfigurationTransaction();
			transaction.Commit();
			pollTask = poller.PollAsync(accountId, CancellationToken.None);

			Assert.False(pollTransportStarted);
			Assert.False(pollTask.IsCompleted);
		}
		finally
		{
			await session.DisposeAsync();
		}

		CodexUsagePollResult result = await pollTask!.WaitAsync(
			TimeSpan.FromSeconds(5));
		Assert.True(pollTransportStarted);
		Assert.Equal("plus", result.PlanType);
	}

	[Fact]
	public async Task SharedOperationGate_DifferentAccountsCanProceedConcurrently()
	{
		CodexAccountOperationGate operationGate = new();
		Guid firstAccountId = Guid.NewGuid();
		Guid secondAccountId = Guid.NewGuid();
		using IDisposable firstLease = await operationGate.EnterAsync(
			firstAccountId,
			CancellationToken.None);

		ValueTask<IDisposable> secondLeaseTask = operationGate.EnterAsync(
			secondAccountId,
			CancellationToken.None);

		Assert.True(secondLeaseTask.IsCompletedSuccessfully);
		using IDisposable secondLease = await secondLeaseTask;
	}

	[Fact]
	public async Task OperationGate_WhenAccountContainmentIsCompromised_RemainsScopedAfterPurge()
	{
		CodexAccountOperationGate operationGate = new();
		Guid compromisedAccountId = Guid.NewGuid();
		Guid healthyAccountId = Guid.NewGuid();
		operationGate.MarkContainmentCompromised(compromisedAccountId);

		operationGate.RequestPurge(compromisedAccountId);

		await Assert.ThrowsAsync<CodexCliContainmentException>(() =>
			operationGate.EnterAsync(
				compromisedAccountId,
				CancellationToken.None).AsTask());
		using IDisposable healthyLease = await operationGate.EnterAsync(
			healthyAccountId,
			CancellationToken.None);
	}

	[Fact]
	public async Task WaitForCompletionAsync_WhenLoginFails_ThrowsAndAbortsTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		Guid previousWorkspaceId = Guid.NewGuid();
		List<FakeTransport> transports = new();
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo =>
			{
				int transportIndex = transports.Count;
				FakeTransport transport = new(
					startInfo,
					new[]
					{
						"""{"id":1,"result":{}}""",
						"""{"id":10,"result":{"type":"chatgpt","loginId":"login-1","authUrl":"https://auth.openai.com/codex/login"}}""",
						"""{"method":"account/login/completed","params":{"loginId":"login-1","success":false,"error":"provider rejected login"}}"""
					},
					configuredWorkspaceValues: transportIndex switch
					{
						0 or 3 => [previousWorkspaceId.ToString("D")],
						1 => [WorkspaceId.ToString("D")],
						_ => []
					});
				transports.Add(transport);
				return transport;
			});

		await using (ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None))
		{
			await Assert.ThrowsAsync<CodexAccountLoginException>(() =>
				session.WaitForCompletionAsync(CancellationToken.None));
			Assert.True(transports[1].IsAborted);
		}

		Assert.Equal(4, transports.Count);
		Assert.True(transports[1].IsDisposed);
		using JsonDocument restoreRequest = JsonDocument.Parse(
			transports[2].WrittenMessages.Single(message =>
				string.Equals(
					GetMethod(message),
					"config/value/write",
					StringComparison.Ordinal)));
		Assert.Equal(
			previousWorkspaceId.ToString("D"),
			restoreRequest.RootElement
				.GetProperty("params")
				.GetProperty("value")
				.GetString());
	}

	[Fact]
	public async Task WaitForCompletionAsync_WhenCallerCancels_PropagatesAndAbortsTransport()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory.Path);
		FakeTransport? transport = null;
		CodexAccountLogin login = CreateLogin(
			Path.Combine(temporaryDirectory.Path, "codex-home"),
			executablePath,
			startInfo => transport = new FakeTransport(
				startInfo,
				CreateStartResponses(),
				blockWhenResponsesAreExhausted: true));

		await using (ICodexAccountLoginSession session = await login.StartAsync(
			Guid.NewGuid(),
			WorkspaceId,
			CancellationToken.None))
		{
			using CancellationTokenSource cancellationSource = new(
				TimeSpan.FromMilliseconds(100));

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				session.WaitForCompletionAsync(cancellationSource.Token));
			Assert.NotNull(transport);
			Assert.True(transport.IsAborted);
		}

		Assert.True(transport!.IsDisposed);
	}

	private static CodexAccountLogin CreateLogin(
		string homeDirectory,
		string executablePath,
		Func<ProcessStartInfo, ICodexAppServerTransport> transportFactory)
	{
		return new CodexAccountLogin(
			_ => homeDirectory,
			() => executablePath,
			transportFactory);
	}

	private static CodexAccountLogin CreateContainmentCompromisingLogin(
		string homeDirectory,
		string executablePath,
		WindowsOfficialCliExecutableLease executableLease,
		CodexAccountOperationGate operationGate,
		List<FakeTransport> transports)
	{
		return new CodexAccountLogin(
			_ => homeDirectory,
			() => new CodexCliExecutableResolution(
				executablePath,
				VersionEvidence: null,
				executableLease),
			(startInfo, operationTracker) =>
			{
				int transportIndex = transports.Count;
				FakeTransport transport = new(
					startInfo,
					CreateSuccessfulResponses(),
					disposeAction: transportIndex == 1
						? operationTracker.MarkContainmentCompromised
						: null);
				transports.Add(transport);
				return transport;
			},
			operationGate,
			TimeSpan.FromSeconds(5));
	}

	private static string[] CreateSuccessfulResponses()
	{
		return new[]
		{
			"""{"id":1,"result":{}}""",
			"""{"id":10,"result":{"type":"chatgpt","loginId":"login-1","authUrl":"https://auth.openai.com/codex/login?state=test"}}""",
			"""{"method":"account/login/completed","params":{"loginId":"login-1","success":true,"error":null}}""",
			"""{"id":11,"result":{"account":{"type":"chatgpt","email":" User@Example.COM ","planType":"plus"},"requiresOpenaiAuth":true}}"""
		};
	}

	private static string[] CreateStartResponses()
	{
		return new[]
		{
			"""{"id":1,"result":{}}""",
			"""{"id":10,"result":{"type":"chatgpt","loginId":"login-1","authUrl":"https://auth.openai.com/codex/login"}}"""
		};
	}

	private static string[] CreateSuccessfulPollResponses()
	{
		return new[]
		{
			"""{"id":1,"result":{}}""",
			"""{"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"plus"},"requiresOpenaiAuth":true}}""",
			"""{"id":3,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1784100000},"secondary":null},"rateLimitsByLimitId":null,"rateLimitResetCredits":null}}"""
		};
	}

	private static string CreateExecutable(string directory)
	{
		string executablePath = Path.Combine(directory, "codex.exe");
		File.WriteAllBytes(executablePath, Array.Empty<byte>());
		return executablePath;
	}

	private static string GetMethod(string message)
	{
		using JsonDocument document = JsonDocument.Parse(message);
		return document.RootElement.GetProperty("method").GetString()!;
	}
}
