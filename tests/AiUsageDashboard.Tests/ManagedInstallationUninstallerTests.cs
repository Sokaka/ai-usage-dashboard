using AiUsageDashboard.Updater;
using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Tests;

public sealed class ManagedInstallationUninstallerTests
{
	private const string ArchiveHash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Fact]
	public async Task UninstallAsync_RemovesOwnedProgramTreesAndPreservesUserData()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		CreateOwnedSupportTrees(layout.InstallRoot);
		Directory.CreateDirectory(layout.UserDataRoot);
		string userDataCanary = Path.Combine(layout.UserDataRoot, "settings.json");
		File.WriteAllText(userDataCanary, "keep");
		List<string> events = [];
		FakeShutdown shutdown = new(events);
		FakeRegistrationStore registration = new(layout.InstallRoot, events);
		FakeLogonStartupRegistrationStore logonStartupRegistration = new(
			events,
			LogonStartupRegistrationState.ExactMatch,
			() => Assert.True(File.Exists(
				ManagedInstallationPaths.GetInstalledAppExecutable(
					layout.InstallRoot))));
		FakeMaintenanceCleanup maintenanceCleanup = new(events);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			shutdown,
			registration,
			maintenanceCleanup,
			events,
			logonStartupRegistration);

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.False(result.WasAlreadyAbsent);
		Assert.True(result.InstallRootRemoved);
		Assert.Null(result.Warning);
		Assert.False(Directory.Exists(layout.InstallRoot));
		Assert.True(File.Exists(userDataCanary));
		Assert.True(registration.WasRemoved);
		Assert.True(logonStartupRegistration.WasRemoved);
		Assert.Equal(1, maintenanceCleanup.CallCount);
		Assert.Equal(
		[
			"quiescent",
			"mutex-acquired",
			"final-scan",
			"startup-remove",
			"mutex-disposed",
			"registry-remove",
			"maintenance-cleanup"
		],
			events);
	}

	[Fact]
	public async Task UninstallAsync_HoldsInstallLockThroughSharedStateCleanup()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		FakeMaintenanceCleanup maintenanceCleanup = new(
			[],
			() => Assert.Throws<UpdateInstallLockException>(() =>
				UpdateInstallLock.AcquireExisting(layout.InstallRoot)));
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(layout.InstallRoot, []),
			maintenanceCleanup,
			[]);

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.True(result.InstallRootRemoved);
		Assert.Equal(1, maintenanceCleanup.CallCount);
	}

	[Fact]
	public async Task UninstallAsync_WhenShutdownFails_DoesNotDeletePayload()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		FakeShutdown shutdown = new([], failQuiescent: true);
		FakeRegistrationStore registration = new(layout.InstallRoot, []);
		FakeMaintenanceCleanup maintenanceCleanup = new([]);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			shutdown,
			registration,
			maintenanceCleanup,
			[]);

		await Assert.ThrowsAsync<RunningPayloadProcessGateException>(() =>
			uninstaller.UninstallAsync(
				layout.ExternalUpdaterPath,
				layout.InstallRoot,
				layout.MaintenanceRoot,
				layout.UserDataRoot,
				shouldManageInstalledAppRegistration: true));

		Assert.True(File.Exists(
			ManagedInstallationPaths.GetInstalledAppExecutable(layout.InstallRoot)));
		Assert.False(registration.WasRemoved);
		Assert.Equal(0, maintenanceCleanup.CallCount);
	}

	[Fact]
	public async Task UninstallAsync_WhenUpdaterRunsInsideInstallRoot_FailsBeforeMutation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		string updaterInsideRoot = Path.Combine(
			layout.InstallRoot,
			"updater-cache",
			"updater.exe");
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(layout.InstallRoot, []),
			new FakeMaintenanceCleanup([]),
			[]);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			uninstaller.UninstallAsync(
				updaterInsideRoot,
				layout.InstallRoot,
				layout.MaintenanceRoot,
				layout.UserDataRoot,
				shouldManageInstalledAppRegistration: true));

		Assert.True(File.Exists(
			ManagedInstallationPaths.GetInstalledAppExecutable(layout.InstallRoot)));
	}

	[Fact]
	public async Task UninstallAsync_WithUnknownTopLevelEntry_PreservesUnknownFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		string unknownFile = Path.Combine(layout.InstallRoot, "user-note.txt");
		File.WriteAllText(unknownFile, "keep");
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(layout.InstallRoot, []),
			new FakeMaintenanceCleanup([]),
			[]);

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.False(result.InstallRootRemoved);
		Assert.Equal(Path.GetFullPath(layout.InstallRoot), result.PreservedInstallRoot);
		Assert.True(File.Exists(unknownFile));
		Assert.False(Directory.Exists(
			ManagedInstallationPaths.GetCurrentDirectory(layout.InstallRoot)));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task UninstallAsync_WhenOwnedTreeContainsJunction_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		string transactionsRoot = Path.Combine(layout.InstallRoot, "transactions");
		string outsideRoot = Path.Combine(temporaryDirectory.Path, "outside");
		string junctionPath = Path.Combine(transactionsRoot, "junction");
		Directory.CreateDirectory(transactionsRoot);
		Directory.CreateDirectory(outsideRoot);
		string canary = Path.Combine(outsideRoot, "canary.txt");
		File.WriteAllText(canary, "keep");
		await JunctionTestHelper.CreateAsync(junctionPath, outsideRoot);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(layout.InstallRoot, []),
			new FakeMaintenanceCleanup([]),
			[]);

		try
		{
			await Assert.ThrowsAsync<InvalidDataException>(() =>
				uninstaller.UninstallAsync(
					layout.ExternalUpdaterPath,
					layout.InstallRoot,
					layout.MaintenanceRoot,
					layout.UserDataRoot,
					shouldManageInstalledAppRegistration: true));
			Assert.True(File.Exists(canary));
			Assert.True(File.Exists(
				ManagedInstallationPaths.GetInstalledAppExecutable(
					layout.InstallRoot)));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionPath);
		}
	}

	[Fact]
	public async Task UninstallAsync_WithReceiptAndMissingCurrent_ResumesSafely()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		UpdateManifest manifest = CreateInstalledPayload(layout.InstallRoot);
		CreateOwnedSupportTrees(layout.InstallRoot);
		await ManagedUninstallReceipt.WriteNewAsync(
			ManagedInstallationPaths.GetUninstallReceipt(layout.InstallRoot),
			layout.InstallRoot,
			manifest);
		Directory.Delete(
			ManagedInstallationPaths.GetCurrentDirectory(layout.InstallRoot),
			recursive: true);
		FakeShutdown shutdown = new([]);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			shutdown,
			new FakeRegistrationStore(layout.InstallRoot, []),
			new FakeMaintenanceCleanup([]),
			[]);

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.True(result.InstallRootRemoved);
		Assert.Equal(0, shutdown.QuiescentCount);
		Assert.True(shutdown.FinalScanCount >= 2);
	}

	[Fact]
	public async Task UninstallAsync_WithCustomRootReceipt_ResumesSafely()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		UpdateManifest manifest = CreateInstalledPayload(layout.InstallRoot);
		CreateOwnedSupportTrees(layout.InstallRoot);
		await ManagedUninstallReceipt.WriteNewAsync(
			ManagedInstallationPaths.GetUninstallReceipt(layout.InstallRoot),
			layout.InstallRoot,
			manifest);
		Directory.Delete(
			ManagedInstallationPaths.GetCurrentDirectory(layout.InstallRoot),
			recursive: true);
		FakeRegistrationStore registration = new(layout.InstallRoot, []);
		FakeLogonStartupRegistrationStore logonStartupRegistration = new(
			[],
			LogonStartupRegistrationState.ExactMatch);
		FakeMaintenanceCleanup maintenanceCleanup = new([]);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			registration,
			maintenanceCleanup,
			[],
			logonStartupRegistration);

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: false);

		Assert.True(result.InstallRootRemoved);
		Assert.False(registration.WasRemoved);
		Assert.Equal(0, logonStartupRegistration.RemoveCount);
		Assert.Equal(0, maintenanceCleanup.CallCount);
	}

	[Fact]
	public async Task UninstallAsync_WhenLogonStartupRemovalFails_PreservesPayloadAndReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		FakeRegistrationStore registration = new(layout.InstallRoot, []);
		FakeMaintenanceCleanup maintenanceCleanup = new([]);
		FakeLogonStartupRegistrationStore logonStartupRegistration = new(
			[],
			LogonStartupRegistrationState.ExactMatch,
			failRemoval: true);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			registration,
			maintenanceCleanup,
			[],
			logonStartupRegistration);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			uninstaller.UninstallAsync(
				layout.ExternalUpdaterPath,
				layout.InstallRoot,
				layout.MaintenanceRoot,
				layout.UserDataRoot,
				shouldManageInstalledAppRegistration: true));

		Assert.True(File.Exists(
			ManagedInstallationPaths.GetInstalledAppExecutable(
				layout.InstallRoot)));
		Assert.True(File.Exists(
			ManagedInstallationPaths.GetUninstallReceipt(layout.InstallRoot)));
		Assert.False(registration.WasRemoved);
		Assert.Equal(0, maintenanceCleanup.CallCount);
		Assert.Equal(1, logonStartupRegistration.RemoveCount);
	}

	[Fact]
	public async Task UninstallAsync_WhenLogonStartupRegistrationConflicts_PreservesValueAndWarns()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		FakeRegistrationStore registration = new(layout.InstallRoot, []);
		FakeLogonStartupRegistrationStore logonStartupRegistration = new(
			[],
			LogonStartupRegistrationState.Conflict);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			registration,
			new FakeMaintenanceCleanup([]),
			[],
			logonStartupRegistration);

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.True(result.InstallRootRemoved);
		Assert.False(logonStartupRegistration.WasRemoved);
		Assert.Equal(1, logonStartupRegistration.RemoveCount);
		Assert.True(registration.WasRemoved);
		Assert.NotNull(result.Warning);
		Assert.Contains("內容與此安裝不符", result.Warning, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UninstallAsync_WhenLogonStartupCommandIsTooLong_ContinuesWithWarning()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		FakeRegistrationStore registration = new(layout.InstallRoot, []);
		FakeLogonStartupRegistrationStore logonStartupRegistration = new(
			[],
			LogonStartupRegistrationState.ExactMatch);
		FakeMaintenanceCleanup maintenanceCleanup = new([]);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			registration,
			maintenanceCleanup,
			[],
			logonStartupRegistration,
			() => throw new ArgumentException("Command is too long."));

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.True(result.InstallRootRemoved);
		Assert.True(registration.WasRemoved);
		Assert.Equal(0, logonStartupRegistration.RemoveCount);
		Assert.Equal(1, maintenanceCleanup.CallCount);
		Assert.NotNull(result.Warning);
		Assert.Contains("登入啟動命令過長", result.Warning, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UninstallAsync_WhenRegistrationRemovalFails_PreservesReceipt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		FakeMaintenanceCleanup maintenanceCleanup = new([]);
		ManagedInstallationUninstaller failingUninstaller = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(
				layout.InstallRoot,
				[],
				failRemoval: true),
			maintenanceCleanup,
			[]);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			failingUninstaller.UninstallAsync(
				layout.ExternalUpdaterPath,
				layout.InstallRoot,
				layout.MaintenanceRoot,
				layout.UserDataRoot,
				shouldManageInstalledAppRegistration: true));

		Assert.False(Directory.Exists(
			ManagedInstallationPaths.GetCurrentDirectory(layout.InstallRoot)));
		Assert.True(File.Exists(
			ManagedInstallationPaths.GetUninstallReceipt(layout.InstallRoot)));
		Assert.Equal(0, maintenanceCleanup.CallCount);

		ManagedInstallationUninstaller retry = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(layout.InstallRoot, []),
			new FakeMaintenanceCleanup([]),
			[]);
		ManagedInstallationUninstallResult result = await retry.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.True(result.InstallRootRemoved);
	}

	[Fact]
	public async Task UninstallAsync_WhenInstallLockIsHeld_PreservesInstallation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		CreateInstalledPayload(layout.InstallRoot);
		using UpdateInstallLock heldLock = UpdateInstallLock.AcquireExisting(
			layout.InstallRoot);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(layout.InstallRoot, []),
			new FakeMaintenanceCleanup([]),
			[]);

		await Assert.ThrowsAsync<UpdateInstallLockException>(() =>
			uninstaller.UninstallAsync(
				layout.ExternalUpdaterPath,
				layout.InstallRoot,
				layout.MaintenanceRoot,
				layout.UserDataRoot,
				shouldManageInstalledAppRegistration: true));

		Assert.True(File.Exists(
			ManagedInstallationPaths.GetInstalledAppExecutable(layout.InstallRoot)));
	}

	[Fact]
	public async Task UninstallAsync_WhenInstallRootIsAbsent_RemovesStaleRegistration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		TestLayout layout = CreateLayout(temporaryDirectory.Path);
		FakeRegistrationStore registration = new(layout.InstallRoot, []);
		FakeLogonStartupRegistrationStore logonStartupRegistration = new(
			[],
			LogonStartupRegistrationState.ExactMatch);
		FakeMaintenanceCleanup maintenanceCleanup = new([]);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			registration,
			maintenanceCleanup,
			[],
			logonStartupRegistration);

		ManagedInstallationUninstallResult result = await uninstaller.UninstallAsync(
			layout.ExternalUpdaterPath,
			layout.InstallRoot,
			layout.MaintenanceRoot,
			layout.UserDataRoot,
			shouldManageInstalledAppRegistration: true);

		Assert.True(result.WasAlreadyAbsent);
		Assert.True(registration.WasRemoved);
		Assert.True(logonStartupRegistration.WasRemoved);
		Assert.Equal(1, logonStartupRegistration.RemoveCount);
		Assert.Equal(1, maintenanceCleanup.CallCount);
	}

	[Fact]
	public async Task UninstallAsync_WhenInstallRootOverlapsUserData_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "data");
		CreateInstalledPayload(installRoot);
		ManagedInstallationUninstaller uninstaller = CreateUninstaller(
			new FakeShutdown([]),
			new FakeRegistrationStore(installRoot, []),
			new FakeMaintenanceCleanup([]),
			[]);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			uninstaller.UninstallAsync(
				Path.Combine(temporaryDirectory.Path, "Updater.exe"),
				installRoot,
				Path.Combine(temporaryDirectory.Path, "maintenance"),
				installRoot,
				shouldManageInstalledAppRegistration: true));

		Assert.True(File.Exists(
			ManagedInstallationPaths.GetInstalledAppExecutable(installRoot)));
	}

	private static ManagedInstallationUninstaller CreateUninstaller(
		FakeShutdown shutdown,
		FakeRegistrationStore registration,
		FakeMaintenanceCleanup maintenanceCleanup,
		List<string> events,
		FakeLogonStartupRegistrationStore? logonStartupRegistration = null,
		Func<string>? createExpectedLogonStartupCommand = null)
	{
		return new ManagedInstallationUninstaller(
			shutdown,
			registration,
			logonStartupRegistration ??
				new FakeLogonStartupRegistrationStore(
					events,
					LogonStartupRegistrationState.Absent),
			maintenanceCleanup,
			() => new CallbackDisposable(
				() => events.Add("mutex-acquired"),
				() => events.Add("mutex-disposed")),
			createExpectedLogonStartupCommand);
	}

	private sealed class FakeLogonStartupRegistrationStore :
		ICurrentUserLogonStartupRegistrationStore
	{
		private readonly Action? _onRemove;
		private readonly bool _failRemoval;
		private readonly List<string> _events;
		private readonly LogonStartupRegistrationState _state;

		internal int RemoveCount { get; private set; }

		internal bool WasRemoved { get; private set; }

		internal FakeLogonStartupRegistrationStore(
			List<string> events,
			LogonStartupRegistrationState state,
			Action? onRemove = null,
			bool failRemoval = false)
		{
			_events = events;
			_state = state;
			_onRemove = onRemove;
			_failRemoval = failRemoval;
		}

		public LogonStartupRegistrationState Query(string expectedCommand)
		{
			throw new NotSupportedException();
		}

		public LogonStartupRegistrationState Enable(string expectedCommand)
		{
			throw new NotSupportedException();
		}

		public LogonStartupRegistrationState Overwrite(string expectedCommand)
		{
			throw new NotSupportedException();
		}

		public LogonStartupRegistrationState RemoveIfMatches(
			string expectedCommand)
		{
			Assert.Equal(
				WindowsLogonStartupRegistrationContract.CreateExpectedCommand(),
				expectedCommand);
			RemoveCount++;
			_events.Add("startup-remove");
			_onRemove?.Invoke();

			if (_failRemoval)
			{
				throw new InvalidOperationException(
					"Startup Registry removal failed.");
			}

			if (_state == LogonStartupRegistrationState.ExactMatch)
			{
				WasRemoved = true;
				return LogonStartupRegistrationState.Absent;
			}

			return _state;
		}
	}

	private static TestLayout CreateLayout(string root)
	{
		return new TestLayout(
			Path.Combine(root, "install"),
			Path.Combine(root, "maintenance"),
			Path.Combine(root, "data"),
			Path.Combine(root, "external", "Updater.exe"));
	}

	private static UpdateManifest CreateInstalledPayload(string installRoot)
	{
		string appPath = ManagedInstallationPaths.GetInstalledAppExecutable(
			installRoot);
		Directory.CreateDirectory(Path.GetDirectoryName(appPath)!);
		File.WriteAllText(appPath, "app");
		UpdateManifest manifest = new(
			UpdateManifest.CurrentSchemaVersion,
			UpdateManifest.ExpectedPackageId,
			"1.2.3",
			UpdateManifest.ExpectedRuntimeIdentifier,
			12345,
			ArchiveHash);
		manifest.WriteAsync(
			ManagedInstallationPaths.GetInstalledManifest(installRoot))
			.GetAwaiter()
			.GetResult();
		return manifest;
	}

	private static void CreateOwnedSupportTrees(string installRoot)
	{
		foreach (string directoryName in
			new[] { "transactions", "downloads", "updater-cache" })
		{
			string directory = Path.Combine(installRoot, directoryName);
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "owned.txt"), "owned");
		}
	}

	private sealed record TestLayout(
		string InstallRoot,
		string MaintenanceRoot,
		string UserDataRoot,
		string ExternalUpdaterPath);

	private sealed class CallbackDisposable : IDisposable
	{
		private readonly Action _onDispose;

		internal CallbackDisposable(Action onCreate, Action onDispose)
		{
			_onDispose = onDispose;
			onCreate();
		}

		public void Dispose()
		{
			_onDispose();
		}
	}

	private sealed class FakeShutdown : IManagedPayloadShutdown
	{
		private readonly List<string> _events;
		private readonly bool _failQuiescent;

		internal int FinalScanCount { get; private set; }

		internal int QuiescentCount { get; private set; }

		internal FakeShutdown(
			List<string> events,
			bool failQuiescent = false)
		{
			_events = events;
			_failQuiescent = failQuiescent;
		}

		public Task EnsureQuiescentAsync(
			string currentPayloadRoot,
			string expectedPayloadGenerationId,
			CancellationToken cancellationToken = default)
		{
			QuiescentCount++;
			_events.Add("quiescent");
			cancellationToken.ThrowIfCancellationRequested();

			if (_failQuiescent)
			{
				throw new RunningPayloadProcessGateException("Busy");
			}

			return Task.CompletedTask;
		}

		public void EnsureNoPayloadProcesses(string currentPayloadRoot)
		{
			FinalScanCount++;
			_events.Add("final-scan");
		}
	}

	private sealed class FakeRegistrationStore :
		IInstalledApplicationRegistrationStore
	{
		private readonly bool _failRemoval;
		private readonly List<string> _events;
		private readonly string _installRoot;

		internal bool WasRemoved { get; private set; }

		internal FakeRegistrationStore(
			string installRoot,
			List<string> events,
			bool failRemoval = false)
		{
			_installRoot = Path.GetFullPath(installRoot);
			_events = events;
			_failRemoval = failRemoval;
		}

		public void Upsert(InstalledApplicationRegistration registration)
		{
			throw new NotSupportedException();
		}

		public bool HasMatchingInstallLocation(string installRoot)
		{
			return string.Equals(
				_installRoot,
				Path.GetFullPath(installRoot),
				StringComparison.OrdinalIgnoreCase);
		}

		public bool RemoveIfMatches(string installRoot)
		{
			Assert.True(HasMatchingInstallLocation(installRoot));

			if (_failRemoval)
			{
				throw new InvalidOperationException("Registry removal failed.");
			}

			WasRemoved = true;
			_events.Add("registry-remove");
			return true;
		}
	}

	private sealed class FakeMaintenanceCleanup : IMaintenanceUpdaterCleanup
	{
		private readonly List<string> _events;
		private readonly Action? _onCleanup;

		internal int CallCount { get; private set; }

		internal FakeMaintenanceCleanup(
			List<string> events,
			Action? onCleanup = null)
		{
			_events = events;
			_onCleanup = onCleanup;
		}

		public MaintenanceUpdaterCleanupResult Cleanup(
			string maintenanceRoot,
			string runningUpdaterExecutablePath)
		{
			CallCount++;
			_events.Add("maintenance-cleanup");
			_onCleanup?.Invoke();
			return new MaintenanceUpdaterCleanupResult(true, null);
		}
	}
}
