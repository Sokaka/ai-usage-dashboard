using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class WindowsAntigravityExistingProcessGateTests
{
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

	[Fact]
	public async Task CheckAsync_WhenPathQueryIsDenied_ReturnsIndeterminate()
	{
		WindowsAntigravityExistingProcessGate gate = new(
			getProcessesByName: _ => new[] { Process.GetCurrentProcess() },
			readExecutablePath: processId => throw new Win32Exception(
				5,
				$"Injected access denial for process {processId}."));

		AntigravityExistingProcessGateResult result = await gate.CheckAsync(
			GetFixtureExecutablePath(),
			allowedProcessId: null,
			CancellationToken.None);

		Assert.Equal(AntigravityExistingProcessGateResult.Indeterminate, result);
	}

	[Fact]
	public async Task CheckAsync_WhenEnumerationFails_ReturnsIndeterminate()
	{
		WindowsAntigravityExistingProcessGate gate = new(
			getProcessesByName: processName => throw new Win32Exception(
				5,
				$"Injected enumeration failure for {processName}."));

		AntigravityExistingProcessGateResult result = await gate.CheckAsync(
			GetFixtureExecutablePath(),
			allowedProcessId: null,
			CancellationToken.None);

		Assert.Equal(AntigravityExistingProcessGateResult.Indeterminate, result);
	}

	[Fact]
	public async Task CheckAsync_WhenProcessExitedDuringInspection_ReturnsClear()
	{
		WindowsAntigravityExistingProcessGate gate = new(
			getProcessesByName: _ => new[] { Process.GetCurrentProcess() },
			readExecutablePath: _ => null);

		AntigravityExistingProcessGateResult result = await gate.CheckAsync(
			GetFixtureExecutablePath(),
			allowedProcessId: null,
			CancellationToken.None);

		Assert.Equal(AntigravityExistingProcessGateResult.Clear, result);
	}

	[Fact]
	public async Task CheckAsync_WhenProcessIdIsAllowed_DoesNotQueryItsPath()
	{
		WindowsAntigravityExistingProcessGate gate = new(
			getProcessesByName: _ => new[] { Process.GetCurrentProcess() },
			readExecutablePath: processId => throw new IOException(
				$"Allowed process {processId} must not be queried."));

		AntigravityExistingProcessGateResult result = await gate.CheckAsync(
			GetFixtureExecutablePath(),
			Environment.ProcessId,
			CancellationToken.None);

		Assert.Equal(AntigravityExistingProcessGateResult.Clear, result);
	}

	[Fact]
	public async Task CheckAsync_WhenCanceledBeforeEnumeration_DoesNotInspectProcesses()
	{
		WindowsAntigravityExistingProcessGate gate = new(
			getProcessesByName: processName => throw new IOException(
				$"Canceled inspection must not enumerate {processName}."));
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(async () =>
			await gate.CheckAsync(
				GetFixtureExecutablePath(),
				allowedProcessId: null,
				cancellation.Token));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task CheckAsync_WithRunningFixture_UsesItsFullPathAndRecognizesExit()
	{
		string fixturePath = GetFixtureExecutablePath();
		using TemporaryDirectory temporaryDirectory = new();
		using Process fixture = Process.Start(new ProcessStartInfo
		{
			FileName = fixturePath,
			WorkingDirectory = temporaryDirectory.Path,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			ArgumentList = { "echo" }
		}) ?? throw new InvalidOperationException(
			$"Unable to start terminal fixture {fixturePath} for process-path inspection.");

		try
		{
			Assert.Equal(
				"READY",
				await fixture.StandardOutput.ReadLineAsync().WaitAsync(TestTimeout));
			Assert.Equal(
				Path.GetFullPath(fixturePath),
				WindowsProcessImagePathReader.Read(fixture.Id),
				ignoreCase: true);
			WindowsAntigravityExistingProcessGate gate = new(
				getProcessesByName: processName =>
				{
					Assert.Equal(
						Path.GetFileNameWithoutExtension(fixturePath),
						processName);
					return new[] { Process.GetProcessById(fixture.Id) };
				});

			Assert.Equal(
				AntigravityExistingProcessGateResult.Detected,
				await gate.CheckAsync(
					fixturePath,
					allowedProcessId: null,
					CancellationToken.None));
			Assert.Equal(
				AntigravityExistingProcessGateResult.Clear,
				await gate.CheckAsync(
					Path.Combine(
						temporaryDirectory.Path,
						Path.GetFileName(fixturePath)),
					allowedProcessId: null,
					CancellationToken.None));

			await fixture.StandardInput.WriteLineAsync("complete");
			await fixture.WaitForExitAsync().WaitAsync(TestTimeout);
			Assert.Equal(0, fixture.ExitCode);
			Assert.Null(WindowsProcessImagePathReader.Read(fixture.Id));
		}
		finally
		{
			if (!fixture.HasExited)
			{
				fixture.Kill();
				await fixture.WaitForExitAsync().WaitAsync(TestTimeout);
			}
		}
	}

	private static string GetFixtureExecutablePath()
	{
		Assembly fixtureAssembly = Assembly.Load(
			new AssemblyName("AiUsageDashboard.TerminalFixture"));
		string executablePath = Path.ChangeExtension(
			fixtureAssembly.Location,
			".exe");

		if (!File.Exists(executablePath))
		{
			throw new FileNotFoundException(
				"The terminal fixture apphost was not copied to the test output.",
				executablePath);
		}

		return executablePath;
	}
}
