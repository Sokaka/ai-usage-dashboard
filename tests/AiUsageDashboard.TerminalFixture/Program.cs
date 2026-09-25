using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

if (args.SequenceEqual(new[]
	{
		"-p",
		"/usage",
		"--output-format",
		"stream-json"
	}))
{
	string executablePath = Environment.ProcessPath ??
		throw new InvalidOperationException("Fixture executable path is unavailable.");
	string workingDirectory = AppContext.BaseDirectory;

	if (File.Exists(Path.Combine(workingDirectory, "record-start.mode")))
	{
		await File.WriteAllTextAsync(
			Path.Combine(workingDirectory, "fixture-started.txt"),
			Environment.ProcessId.ToString());
	}

	if (File.Exists(Path.Combine(
		workingDirectory,
		"spawn-child-then-exit.mode")))
	{
		using Process childProcess = Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			ArgumentList = { "wait-for-child-release" }
		}) ?? throw new InvalidOperationException("Unable to start fixture child process.");
		await WriteProcessIdsAsync(
			workingDirectory,
			Environment.ProcessId,
			childProcess.Id);
		Console.Write("fixture-output\r\n");
		Console.Out.Flush();
		WaitForFixtureExitSignal("root");
		return 0;
	}

	if (File.Exists(Path.Combine(
		workingDirectory,
		"spawn-child-and-hang.mode")))
	{
		using Process childProcess = Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			ArgumentList = { "silent-hang" }
		}) ?? throw new InvalidOperationException("Unable to start fixture child process.");
		await WriteProcessIdsAsync(
			workingDirectory,
			Environment.ProcessId,
			childProcess.Id);
		Console.Write("fixture-started\r\n");
		Console.Out.Flush();
		await Task.Delay(Timeout.InfiniteTimeSpan);
		return 0;
	}

	Console.Write("fixture-output\r\n");
	Console.Out.Flush();
	return 0;
}

if (args.Length != 1)
{
	return 2;
}

switch (args[0])
{
	case "--version":
		string codexVersionProbeModePath = Path.Combine(
			AppContext.BaseDirectory,
			"codex-version-probe.mode");

		if (File.Exists(codexVersionProbeModePath))
		{
			string probeMode = (await File.ReadAllTextAsync(
				codexVersionProbeModePath)).Trim();
			string? codexHome =
				Environment.GetEnvironmentVariable("CODEX_HOME");
			string? sqliteHome =
				Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
			string? home = Environment.GetEnvironmentVariable("HOME");
			string? userProfile =
				Environment.GetEnvironmentVariable("USERPROFILE");

			if (string.IsNullOrWhiteSpace(codexHome) ||
				string.IsNullOrWhiteSpace(sqliteHome) ||
				string.IsNullOrWhiteSpace(home) ||
				string.IsNullOrWhiteSpace(userProfile) ||
				!Path.IsPathFullyQualified(codexHome) ||
				!Path.IsPathFullyQualified(sqliteHome) ||
				!Path.IsPathFullyQualified(home) ||
				!Path.IsPathFullyQualified(userProfile))
			{
				return 5;
			}

			await File.WriteAllLinesAsync(
				Path.Combine(
					AppContext.BaseDirectory,
					"codex-version-probe-homes.txt"),
				[
					codexHome,
					sqliteHome,
					home,
					userProfile,
					Environment.CurrentDirectory
				]);
			await File.WriteAllTextAsync(
				Path.Combine(codexHome, "probe-state.txt"),
				"created");
			await File.WriteAllTextAsync(
				Path.Combine(sqliteHome, "probe-state.txt"),
				"created");

			if (string.Equals(
				probeMode,
				"spawn-child-then-exit",
				StringComparison.Ordinal))
			{
				string fixtureExecutablePath = Environment.ProcessPath ??
					throw new InvalidOperationException(
						"Fixture executable path is unavailable.");
				using Process childProcess = Process.Start(new ProcessStartInfo
				{
					FileName = fixtureExecutablePath,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					ArgumentList = { "silent-hang" }
				}) ?? throw new InvalidOperationException(
					"Unable to start the Codex probe fixture child process.");
				await WriteProcessIdsAsync(
					AppContext.BaseDirectory,
					Environment.ProcessId,
					childProcess.Id);
			}

			Console.Write("codex-cli 0.144.1\r\n");
		}
		else
		{
			Console.Write("fixture-1.2.3\r\n");
		}

		Console.Out.Flush();
		return 0;

	case "echo":
		Console.Write("READY\r\n");
		Console.Out.Flush();
		string? input = Console.ReadLine();
		Console.Write($"ECHO:{input}\r\n");
		return 0;

	case "raw-usage":
		Console.Write("READY\r\n");
		Console.Out.Flush();
		Console.Write("\u001b[2 q");
		Console.Out.Flush();
		Console.Write("\u001b[39X");
		Console.Out.Flush();
		Console.Write("\u001b[>4;2m");
		Console.Out.Flush();
		Console.Write("\u001b[=1;1u");
		Console.Out.Flush();
		Console.Write("\u001b[?u");
		Console.Out.Flush();
		byte[] expectedInput = Encoding.UTF8.GetBytes("/usage\r");
		byte[] actualInput = new byte[expectedInput.Length];
		using (Stream standardInput = Console.OpenStandardInput())
		{
			int offset = 0;

			while (offset < actualInput.Length)
			{
				int bytesRead = await standardInput.ReadAsync(
					actualInput.AsMemory(offset));

				if (bytesRead == 0)
				{
					return 3;
				}

				offset += bytesRead;
			}
		}

		if (!CryptographicOperations.FixedTimeEquals(actualInput, expectedInput))
		{
			return 4;
		}

		Console.Write("RAW-OK\r\n");
		Console.Out.Flush();
		return 0;

	case "vt":
		Console.Write("\u001b[?1049h\u001b[2J\u001b[H");
		Console.Write("SYNTHETIC AGY MODEL QUOTAS\r\n");
		Console.Write("box:\u250c\u2500\u2510 utf8:\u6e2c\u8a66\r\n");
		Console.Write("progress:10%\rprogress:100%\r\n");
		Console.Out.Flush();
		return 0;

	case "hang":
		Console.Write($"PID:{Environment.ProcessId}\r\n");
		Console.Out.Flush();
		await Task.Delay(Timeout.InfiniteTimeSpan);
		return 0;

	case "silent-hang":
		await Task.Delay(Timeout.InfiniteTimeSpan);
		return 0;

	case "wait-for-child-release":
		WaitForFixtureExitSignal("child");
		return 0;

	case "spawn-child-and-hang":
		string executablePath = Environment.ProcessPath ??
			throw new InvalidOperationException("Fixture executable path is unavailable.");
		using (Process childProcess = Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			UseShellExecute = false,
			ArgumentList = { "hang" }
		}) ?? throw new InvalidOperationException("Unable to start fixture child process."))
		{
			Console.Write($"ROOT:{Environment.ProcessId};CHILD:{childProcess.Id}\r\n");
			Console.Out.Flush();
			await Task.Delay(Timeout.InfiniteTimeSpan);
		}
		return 0;

	case "oversize":
		const int outputCharacterCount = 64 * 1024;
		char[] output = Enumerable.Repeat('x', outputCharacterCount).ToArray();
		Console.Write(output);
		Console.Write("\r\nEND\r\n");
		Console.Out.Flush();
		return 0;

	default:
		return 2;
}

static void WaitForFixtureExitSignal(string processRole)
{
	string signalPrefix = File.ReadAllText(Path.Combine(
		AppContext.BaseDirectory,
		"spawn-child-then-exit.mode"));
	using EventWaitHandle exitSignal = EventWaitHandle.OpenExisting(
		signalPrefix + "." + processRole);
	if (!exitSignal.WaitOne(TimeSpan.FromSeconds(15)))
	{
		throw new TimeoutException(
			$"The fixture {processRole} did not receive its exit signal '{signalPrefix}'.");
	}
}

static async Task WriteProcessIdsAsync(
	string workingDirectory,
	int rootProcessId,
	int childProcessId)
{
	string destinationPath = Path.Combine(
		workingDirectory,
		"runner-pids.txt");
	string temporaryPath = destinationPath + ".tmp";

	try
	{
		await File.WriteAllTextAsync(
			temporaryPath,
			$"{rootProcessId};{childProcessId}");
		File.Move(temporaryPath, destinationPath, overwrite: true);
	}
	finally
	{
		if (File.Exists(temporaryPath))
		{
			File.Delete(temporaryPath);
		}
	}
}
