using AiUsageDashboard.Updater;

namespace AiUsageDashboard.Tests;

public sealed class UpdaterProcessGateTests
{
	private const string GenerationId =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Fact]
	public async Task EnsureQuiescentAsync_WhenNoPayloadProcessExists_SkipsProtocol()
	{
		FakeProcessProbe processProbe = new([[]]);
		FakeShutdownClient shutdownClient = new();
		RunningPayloadProcessGate gate = CreateGate(processProbe, shutdownClient);

		await gate.EnsureQuiescentAsync(
			@"D:\Programs\AiUsageDashboard\current",
			GenerationId);

		Assert.Equal(0, shutdownClient.QueryCount);
		Assert.Equal(0, shutdownClient.ReserveCount);
	}

	[Fact]
	public async Task EnsureQuiescentAsync_WithExactAcceptedIdentity_WaitsForNaturalExit()
	{
		string currentRoot = @"D:\Programs\AiUsageDashboard\current";
		RunningPayloadProcess dashboard = CreateDashboardProcess(currentRoot);
		ObservedUpdateProcessIdentity identity = CreateObservedIdentity(dashboard);
		FakeProcessProbe processProbe = new(
			[[dashboard], [dashboard], []],
			[true]);
		FakeShutdownClient shutdownClient = new(
			new UpdateShutdownQueryResult(true, identity, null),
			new UpdateShutdownReservationResult(true, identity, null));
		RunningPayloadProcessGate gate = CreateGate(processProbe, shutdownClient);

		await gate.EnsureQuiescentAsync(currentRoot, GenerationId);

		Assert.Equal(1, shutdownClient.QueryCount);
		Assert.Equal(1, shutdownClient.ReserveCount);
		Assert.Equal(identity, shutdownClient.ReservedIdentity);
		Assert.Equal([dashboard], processProbe.WaitedProcesses);
	}

	[Fact]
	public async Task EnsureQuiescentAsync_WhenQueryIdentityDiffers_AbortsBeforeReserve()
	{
		string currentRoot = @"D:\Programs\AiUsageDashboard\current";
		RunningPayloadProcess dashboard = CreateDashboardProcess(currentRoot);
		ObservedUpdateProcessIdentity wrongIdentity =
			CreateObservedIdentity(dashboard) with
			{
				ProcessStartTimeUtcTicks = dashboard.ProcessStartTimeUtcTicks + 1
			};
		FakeProcessProbe processProbe = new([[dashboard]]);
		FakeShutdownClient shutdownClient = new(
			new UpdateShutdownQueryResult(true, wrongIdentity, null),
			reservation: null);
		RunningPayloadProcessGate gate = CreateGate(processProbe, shutdownClient);

		await Assert.ThrowsAsync<RunningPayloadProcessGateException>(() =>
			gate.EnsureQuiescentAsync(currentRoot, GenerationId));

		Assert.Equal(0, shutdownClient.ReserveCount);
		Assert.Empty(processProbe.WaitedProcesses);
	}

	[Fact]
	public async Task EnsureQuiescentAsync_WhenReserveIsRejected_DoesNotWaitOrKill()
	{
		string currentRoot = @"D:\Programs\AiUsageDashboard\current";
		RunningPayloadProcess dashboard = CreateDashboardProcess(currentRoot);
		ObservedUpdateProcessIdentity identity = CreateObservedIdentity(dashboard);
		FakeProcessProbe processProbe = new([[dashboard], [dashboard]]);
		FakeShutdownClient shutdownClient = new(
			new UpdateShutdownQueryResult(true, identity, null),
			new UpdateShutdownReservationResult(
				false,
				identity,
				"Busy"));
		RunningPayloadProcessGate gate = CreateGate(processProbe, shutdownClient);

		await Assert.ThrowsAsync<RunningPayloadProcessGateException>(() =>
			gate.EnsureQuiescentAsync(currentRoot, GenerationId));

		Assert.Empty(processProbe.WaitedProcesses);
	}

	[Fact]
	public async Task EnsureQuiescentAsync_WhenProcessDoesNotExitNaturally_Aborts()
	{
		string currentRoot = @"D:\Programs\AiUsageDashboard\current";
		RunningPayloadProcess dashboard = CreateDashboardProcess(currentRoot);
		ObservedUpdateProcessIdentity identity = CreateObservedIdentity(dashboard);
		FakeProcessProbe processProbe = new(
			[[dashboard], [dashboard]],
			[false]);
		FakeShutdownClient shutdownClient = new(
			new UpdateShutdownQueryResult(true, identity, null),
			new UpdateShutdownReservationResult(true, identity, null));
		RunningPayloadProcessGate gate = CreateGate(processProbe, shutdownClient);

		await Assert.ThrowsAsync<RunningPayloadProcessGateException>(() =>
			gate.EnsureQuiescentAsync(currentRoot, GenerationId));
	}

	[Fact]
	public async Task EnsureQuiescentAsync_WhenFinalRescanFindsProcess_Aborts()
	{
		string currentRoot = @"D:\Programs\AiUsageDashboard\current";
		RunningPayloadProcess dashboard = CreateDashboardProcess(currentRoot);
		ObservedUpdateProcessIdentity identity = CreateObservedIdentity(dashboard);
		FakeProcessProbe processProbe = new(
			[[dashboard], [dashboard], [dashboard]],
			[true]);
		FakeShutdownClient shutdownClient = new(
			new UpdateShutdownQueryResult(true, identity, null),
			new UpdateShutdownReservationResult(true, identity, null));
		RunningPayloadProcessGate gate = CreateGate(processProbe, shutdownClient);

		await Assert.ThrowsAsync<RunningPayloadProcessGateException>(() =>
			gate.EnsureQuiescentAsync(currentRoot, GenerationId));
	}

	[Fact]
	public async Task EnsureQuiescentAsync_WhenOnlyHelperIsRunning_FailsClosed()
	{
		string currentRoot = @"D:\Programs\AiUsageDashboard\current";
		RunningPayloadProcess helper = new(
			123,
			456,
			Path.GetFullPath(Path.Combine(
				currentRoot,
				"app",
				"AiUsageDashboard.Antigravity.Setup.exe")));
		FakeProcessProbe processProbe = new([[helper]]);
		FakeShutdownClient shutdownClient = new();
		RunningPayloadProcessGate gate = CreateGate(processProbe, shutdownClient);

		await Assert.ThrowsAsync<RunningPayloadProcessGateException>(() =>
			gate.EnsureQuiescentAsync(currentRoot, GenerationId));

		Assert.Equal(0, shutdownClient.QueryCount);
	}

	[Fact]
	public void EnsureNoPayloadProcesses_WhenFinalScanFindsProcess_FailsClosed()
	{
		string currentRoot = @"D:\Programs\AiUsageDashboard\current";
		RunningPayloadProcess dashboard = CreateDashboardProcess(currentRoot);
		FakeProcessProbe processProbe = new([[dashboard]]);
		RunningPayloadProcessGate gate = CreateGate(
			processProbe,
			new FakeShutdownClient());

		Assert.Throws<RunningPayloadProcessGateException>(() =>
			gate.EnsureNoPayloadProcesses(currentRoot));
	}

	[Fact]
	public void DashboardProcessOutsideCurrentPayload_FailsClosed()
	{
		string expectedPath = Path.GetFullPath(
			@"D:\Programs\AiUsageDashboard\current\app\AiUsageDashboard.App.exe");
		string unexpectedPath = Path.GetFullPath(
			@"D:\Portable\AiUsageDashboard.App.exe");

		Assert.Throws<RunningPayloadProcessGateException>(() =>
			SystemRunningPayloadProcessProbe.EnsureDashboardExecutablePathMatches(
				expectedPath,
				unexpectedPath,
				123));
	}

	[Fact]
	public void DashboardProcessAtExactCurrentPayload_IsAccepted()
	{
		string expectedPath = Path.GetFullPath(
			@"D:\Programs\AiUsageDashboard\current\app\AiUsageDashboard.App.exe");

		SystemRunningPayloadProcessProbe.EnsureDashboardExecutablePathMatches(
			expectedPath,
			expectedPath.ToUpperInvariant(),
			123);
	}

	private static RunningPayloadProcessGate CreateGate(
		IRunningPayloadProcessProbe processProbe,
		IUpdateShutdownProtocolClient shutdownClient)
	{
		return new RunningPayloadProcessGate(
			processProbe,
			shutdownClient,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(1));
	}

	private static RunningPayloadProcess CreateDashboardProcess(string currentRoot)
	{
		return new RunningPayloadProcess(
			123,
			456,
			Path.GetFullPath(Path.Combine(
				currentRoot,
				"app",
				"AiUsageDashboard.App.exe")));
	}

	private static ObservedUpdateProcessIdentity CreateObservedIdentity(
		RunningPayloadProcess process)
	{
		return new ObservedUpdateProcessIdentity(
			process.ProcessId,
			process.ProcessStartTimeUtcTicks,
			GenerationId);
	}

	private sealed class FakeProcessProbe : IRunningPayloadProcessProbe
	{
		private readonly Queue<IReadOnlyList<RunningPayloadProcess>> _captures;
		private readonly Queue<bool> _waitResults;

		internal FakeProcessProbe(
			IEnumerable<IReadOnlyList<RunningPayloadProcess>> captures,
			IEnumerable<bool>? waitResults = null)
		{
			_captures = new Queue<IReadOnlyList<RunningPayloadProcess>>(captures);
			_waitResults = new Queue<bool>(waitResults ?? []);
		}

		internal List<RunningPayloadProcess> WaitedProcesses { get; } = [];

		public IReadOnlyList<RunningPayloadProcess> Capture(
			string currentPayloadRoot)
		{
			Assert.NotEmpty(currentPayloadRoot);
			return _captures.Dequeue();
		}

		public Task<bool> WaitForNaturalExitAsync(
			RunningPayloadProcess process,
			TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			Assert.True(timeout > TimeSpan.Zero);
			cancellationToken.ThrowIfCancellationRequested();
			WaitedProcesses.Add(process);
			return Task.FromResult(_waitResults.Dequeue());
		}
	}

	private sealed class FakeShutdownClient : IUpdateShutdownProtocolClient
	{
		private readonly UpdateShutdownQueryResult? _query;
		private readonly UpdateShutdownReservationResult? _reservation;

		internal FakeShutdownClient(
			UpdateShutdownQueryResult? query = null,
			UpdateShutdownReservationResult? reservation = null)
		{
			_query = query;
			_reservation = reservation;
		}

		internal int QueryCount { get; private set; }

		internal int ReserveCount { get; private set; }

		internal ObservedUpdateProcessIdentity? ReservedIdentity { get; private set; }

		public Task<UpdateShutdownQueryResult> QueryAsync(
			TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			QueryCount++;
			Assert.True(timeout > TimeSpan.Zero);
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(_query ??
				new UpdateShutdownQueryResult(false, null, "Unavailable"));
		}

		public Task<UpdateShutdownReservationResult> ReserveAsync(
			ObservedUpdateProcessIdentity identity,
			TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			ReserveCount++;
			ReservedIdentity = identity;
			Assert.True(timeout > TimeSpan.Zero);
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(_reservation ??
				new UpdateShutdownReservationResult(
					false,
					null,
					"Unavailable"));
		}
	}
}
