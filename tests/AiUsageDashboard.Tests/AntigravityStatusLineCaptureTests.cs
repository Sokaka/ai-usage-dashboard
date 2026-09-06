using System.Diagnostics;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityStatusLineCaptureTests
{
	[Fact]
	public async Task AppBuildOutput_CaptureHelperIsStandaloneAndRunnable()
	{
		string configuration = new DirectoryInfo(AppContext.BaseDirectory)
			.Parent?.Name ?? throw new InvalidOperationException(
				"Unable to resolve the test build configuration.");
		string appOutputDirectory = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"bin",
			configuration,
			"net8.0-windows");
		string helperPath = Path.Combine(
			appOutputDirectory,
			"AiUsageDashboard.AntigravityCapture.exe");

		Assert.True(File.Exists(helperPath), $"Capture helper is missing: {helperPath}");
		Assert.False(File.Exists(Path.Combine(
			appOutputDirectory,
			"AiUsageDashboard.AntigravityCapture.dll")));

		ProcessStartInfo startInfo = new(helperPath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(
			AntigravityStatusLineCapture.SmokeTestArgument);
		using Process process = new() { StartInfo = startInfo };

		Assert.True(process.Start());
		try
		{
			using CancellationTokenSource testTimeout = new(
				TimeSpan.FromSeconds(10));
			await process.WaitForExitAsync(testTimeout.Token);

			Assert.Equal(0, process.ExitCode);
			Assert.Equal(
				string.Empty,
				await process.StandardOutput.ReadToEndAsync());
			Assert.Equal(
				string.Empty,
				await process.StandardError.ReadToEndAsync());
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync();
			}
		}
	}

	[Fact]
	public async Task CaptureHelper_WithOpenSilentStandardInput_ExitsWithoutOutput()
	{
		string configuration = new DirectoryInfo(AppContext.BaseDirectory)
			.Parent?.Name ?? throw new InvalidOperationException(
				"Unable to resolve the test build configuration.");
		string helperPath = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.AntigravityCapture",
			"bin",
			configuration,
			"net8.0-windows",
			"AiUsageDashboard.AntigravityCapture.exe");
		Assert.True(File.Exists(helperPath), $"Capture helper is missing: {helperPath}");

		ProcessStartInfo startInfo = new(helperPath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(
			AntigravityStatusLineCapture.MarkerArgument);
		using Process process = new() { StartInfo = startInfo };
		Stopwatch elapsed = Stopwatch.StartNew();

		Assert.True(process.Start());
		try
		{
			using CancellationTokenSource testTimeout = new(
				TimeSpan.FromSeconds(6));
			await process.WaitForExitAsync(testTimeout.Token);
			elapsed.Stop();

			Assert.InRange(
				elapsed.Elapsed,
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(4));
			Assert.Equal(0, process.ExitCode);
			Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
			Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync());
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync();
			}
		}
	}

	[Fact]
	public async Task CaptureHelper_SmokeTest_ExercisesPrivateWriteReadPathAndExitsSuccessfully()
	{
		string configuration = new DirectoryInfo(AppContext.BaseDirectory)
			.Parent?.Name ?? throw new InvalidOperationException(
				"Unable to resolve the test build configuration.");
		string helperPath = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.AntigravityCapture",
			"bin",
			configuration,
			"net8.0-windows",
			"AiUsageDashboard.AntigravityCapture.exe");
		Assert.True(File.Exists(helperPath), $"Capture helper is missing: {helperPath}");
		ProcessStartInfo startInfo = new(helperPath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(
			AntigravityStatusLineCapture.SmokeTestArgument);
		using Process process = new() { StartInfo = startInfo };

		Assert.True(process.Start());
		using CancellationTokenSource testTimeout = new(TimeSpan.FromSeconds(5));
		await process.WaitForExitAsync(testTimeout.Token);

		Assert.Equal(0, process.ExitCode);
		Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
		Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync());
	}

	private static readonly DateTimeOffset CapturedAt =
		new(2026, 8, 12, 1, 2, 3, TimeSpan.Zero);

	[Fact]
	public void TryParsePayload_WithOfficialEmailAndPlanTier_ReturnsDisplayValue()
	{
		byte[] payload = Encoding.UTF8.GetBytes(
			"{\"model\":{\"display_name\":\"Gemini\"}," +
			"\"email\":\"person@example.com\",\"plan_tier\":\"Pro\"}");

		bool parsed = AntigravityStatusLineCapture.TryParsePayload(
			payload,
			CapturedAt,
			out AntigravityStatusLineAccountDisplay? display);

		Assert.True(parsed);
		Assert.Equal("person@example.com", display!.Email);
		Assert.Equal(CapturedAt, display.CapturedAtUtc);
		Assert.Equal("Pro", display.PlanTier);
	}

	[Theory]
	[InlineData("{\"email\":\"person@example.com\"}")]
	[InlineData("{\"email\":\"person@example.com\",\"plan_tier\":null}")]
	public void TryParsePayload_WithoutPlanTier_ReturnsNullPlanTier(string json)
	{
		Assert.True(AntigravityStatusLineCapture.TryParsePayload(
			Encoding.UTF8.GetBytes(json),
			CapturedAt,
			out AntigravityStatusLineAccountDisplay? display));
		Assert.Null(display!.PlanTier);
	}

	[Theory]
	[InlineData("{\"email\":\"person@example.com\",\"plan_tier\":42}")]
	[InlineData("{\"email\":\"person@example.com\",\"plan_tier\":\"\"}")]
	[InlineData("{\"email\":\"person@example.com\",\"plan_tier\":\" Pro\"}")]
	[InlineData("{\"email\":\"person@example.com\",\"plan_tier\":\"Pro\\u0001\"}")]
	[InlineData("{\"email\":\"person@example.com\",\"plan_tier\":\"Pro\",\"plan_tier\":\"Ultra\"}")]
	public void TryParsePayload_WithUnsafePlanTier_DropsPlanTier(string json)
	{
		Assert.True(AntigravityStatusLineCapture.TryParsePayload(
			Encoding.UTF8.GetBytes(json),
			CapturedAt,
			out AntigravityStatusLineAccountDisplay? display));
		Assert.Equal("person@example.com", display!.Email);
		Assert.Null(display.PlanTier);
	}

	[Fact]
	public void TryParsePayload_WithOverlongPlanTier_DropsPlanTier()
	{
		string json =
			$"{{\"email\":\"person@example.com\",\"plan_tier\":\"{new string('p', 65)}\"}}";

		Assert.True(AntigravityStatusLineCapture.TryParsePayload(
			Encoding.UTF8.GetBytes(json),
			CapturedAt,
			out AntigravityStatusLineAccountDisplay? display));
		Assert.Null(display!.PlanTier);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"email\":null}")]
	[InlineData("{\"email\":\" person@example.com\"}")]
	[InlineData("{\"email\":\"person@example.com\",\"email\":\"other@example.com\"}")]
	[InlineData("{\"email\":\"person@-example.com\"}")]
	[InlineData("[]")]
	public void TryParsePayload_WithMissingOrUnsafeEmail_FailsClosed(
		string json)
	{
		Assert.False(AntigravityStatusLineCapture.TryParsePayload(
			Encoding.UTF8.GetBytes(json),
			CapturedAt,
			out AntigravityStatusLineAccountDisplay? display));
		Assert.Null(display);
	}

	[Fact]
	public void TryParsePayload_WithInvalidUtf8_FailsClosed()
	{
		byte[] payload =
		{
			(byte)'{', (byte)'\"', (byte)'x', (byte)'\"', (byte)':',
			(byte)'\"', 0xC3, 0x28, (byte)'\"', (byte)',',
			(byte)'\"', (byte)'e', (byte)'m', (byte)'a', (byte)'i',
			(byte)'l', (byte)'\"', (byte)':', (byte)'\"', (byte)'a',
			(byte)'@', (byte)'b', (byte)'.', (byte)'c', (byte)'o',
			(byte)'m', (byte)'\"', (byte)'}'
		};

		Assert.False(AntigravityStatusLineCapture.TryParsePayload(
			payload,
			CapturedAt,
			out _));
	}

	[Fact]
	public void TryParsePayload_OverMaximumSize_FailsClosed()
	{
		byte[] payload = new byte[
			AntigravityStatusLineCapture.MaximumPayloadBytes + 1];

		Assert.False(AntigravityStatusLineCapture.TryParsePayload(
			payload,
			CapturedAt,
			out _));
	}

	[Fact]
	public void TryParsePayload_OverMaximumDepth_FailsClosed()
	{
		string nested = new string('[', 33) + new string(']', 33);
		byte[] payload = Encoding.UTF8.GetBytes(
			$"{{\"nested\":{nested},\"email\":\"person@example.com\"}}");

		Assert.False(AntigravityStatusLineCapture.TryParsePayload(
			payload,
			CapturedAt,
			out _));
	}

	[Fact]
	public void CapturePath_IsMachineLocalAndDoesNotContainIdentity()
	{
		string path = AntigravityStatusLineCapture.GetCaptureFilePath();

		Assert.True(Path.IsPathFullyQualified(path));
		Assert.EndsWith(
			Path.Combine(
				"AiUsageDashboard",
				"private",
				"antigravity-statusline",
				"account-display-v1.json"),
			path,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("person@example.com", path);
	}

	[Fact]
	public void TryWriteAndRead_UsesPrivateAtomicCaptureFile()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(applicationDirectory);
		string capturePath = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline",
			"account-display-v1.json");
		AntigravityStatusLineAccountDisplay expected = new(
			"person@example.com",
			CapturedAt,
			"Pro");

		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			expected));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(capturePath));
		Assert.Contains(
			"\"planTier\":\"Pro\"",
			File.ReadAllText(capturePath),
			StringComparison.Ordinal);
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal(expected, actual);
		Assert.Empty(Directory.EnumerateFiles(
			Path.GetDirectoryName(capturePath)!,
			"*.tmp"));
	}

	[Fact]
	public void TryRead_LegacyCaptureWithoutPlanTier_ReturnsNullPlanTier()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string captureDirectory = Path.Combine(
			temporaryDirectory.Path,
			"private",
			"antigravity-statusline");
		Assert.True(
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				captureDirectory,
				out _,
				out _));
		string capturePath = Path.Combine(
			captureDirectory,
			AntigravityStatusLineCapture.CaptureFileName);
		File.WriteAllText(
			capturePath,
			$"{{\"schemaVersion\":1,\"email\":\"person@example.com\",\"capturedAtUtc\":\"{CapturedAt:O}\"}}",
			new UTF8Encoding(false, true));
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(capturePath));

		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			out AntigravityStatusLineAccountDisplay? display));
		Assert.Null(display!.PlanTier);
	}

	[Fact]
	public void TryWrite_WithSafelyInheritedLock_RepairsLockAndWrites()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(applicationDirectory);
		string captureDirectory = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline");
		Assert.True(
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				captureDirectory,
				out _,
				out _));
		string lockPath = Path.Combine(
			captureDirectory,
			AntigravityStatusLineCapture.LockFileName);
		using (File.Create(lockPath))
		{
		}
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateFile(lockPath));
		Assert.True(
			AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(lockPath));
		string capturePath = Path.Combine(
			captureDirectory,
			AntigravityStatusLineCapture.CaptureFileName);

		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("person@example.com", CapturedAt)));

		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(lockPath));
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal("person@example.com", actual!.Email);
	}

	[Fact]
	public void TryWrite_WhenNewLockProtectionFails_RemovesSafeInheritedLock()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(applicationDirectory);
		string capturePath = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline",
			AntigravityStatusLineCapture.CaptureFileName);
		string captureDirectory = Path.GetDirectoryName(capturePath)!;

		Assert.False(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("person@example.com", CapturedAt),
			_ => false));

		Assert.False(File.Exists(Path.Combine(
			captureDirectory,
			AntigravityStatusLineCapture.LockFileName)));
		Assert.Empty(Directory.EnumerateFiles(captureDirectory, "*.tmp"));
	}

	[Fact]
	public void TryWrite_WhenTemporaryFileProtectionFails_RemovesSensitiveTemp()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(applicationDirectory);
		string capturePath = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline",
			AntigravityStatusLineCapture.CaptureFileName);
		int protectionAttempt = 0;

		Assert.False(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("person@example.com", CapturedAt),
			path => Interlocked.Increment(ref protectionAttempt) == 1 &&
				AntigravityPrivateKeyAcl.TryProtectNewFile(path)));

		Assert.Equal(2, protectionAttempt);
		Assert.False(File.Exists(capturePath));
		Assert.Empty(Directory.EnumerateFiles(
			Path.GetDirectoryName(capturePath)!,
			"*.tmp"));
	}

	[Fact]
	public void TryWrite_OlderCaptureDoesNotReplaceNewerCapture()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(applicationDirectory);
		string capturePath = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline",
			"account-display-v1.json");
		AntigravityStatusLineAccountDisplay newer = new(
			"newer@example.com",
			CapturedAt.AddMinutes(1));

		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			newer));
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("older@example.com", CapturedAt)));
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal(newer, actual);
	}

	[Fact]
	public void TryWrite_SameEmailWithinThrottleWindow_KeepsCaptureAndSignalsFreshObservation()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(applicationDirectory);
		string capturePath = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline",
			"account-display-v1.json");
		AntigravityStatusLineAccountDisplay first = new(
			"person@example.com",
			CapturedAt);

		Assert.True(AntigravityStatusLineCapture.TryWrite(capturePath, first));
		using AntigravityStatusLineObservationSignal.Listener signal =
			Assert.IsType<AntigravityStatusLineObservationSignal.Listener>(
				AntigravityStatusLineObservationSignal.Listener.TryCreate(
					first.Email));
		Assert.False(signal.TryConsume());
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("PERSON@example.com", CapturedAt.AddSeconds(10))));
		Assert.True(signal.TryConsume());
		Assert.False(signal.TryConsume());
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal(first, actual);
	}

	[Fact]
	public void TryWrite_DifferentEmailWithinThrottleWindow_UpdatesImmediately()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(applicationDirectory);
		string capturePath = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline",
			"account-display-v1.json");
		AntigravityStatusLineAccountDisplay changed = new(
			"other@example.com",
			CapturedAt.AddSeconds(10));

		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("person@example.com", CapturedAt)));
		Assert.True(AntigravityStatusLineCapture.TryWrite(capturePath, changed));
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal(changed, actual);
	}

	[Fact]
	public void TryWrite_ChangedPlanWithinThrottleWindow_UpdatesImmediately()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string capturePath = Path.Combine(
			temporaryDirectory.Path,
			"private",
			"antigravity-statusline",
			AntigravityStatusLineCapture.CaptureFileName);
		AntigravityStatusLineAccountDisplay changed = new(
			"person@example.com",
			CapturedAt.AddSeconds(10),
			"Ultra");

		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("person@example.com", CapturedAt, "Pro")));
		Assert.True(AntigravityStatusLineCapture.TryWrite(capturePath, changed));
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal(changed, actual);
	}

	[Fact]
	public void TryWrite_AfterClockRollback_ReplacesFarFutureDifferentAccount()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string capturePath = Path.Combine(
			temporaryDirectory.Path,
			"private",
			"antigravity-statusline",
			AntigravityStatusLineCapture.CaptureFileName);
		DateTimeOffset now = CapturedAt;
		DateTimeOffset poisonedTimestamp = now.AddDays(2);
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("old@example.com", poisonedTimestamp),
			AntigravityPrivateKeyAcl.TryProtectNewFile,
			getUtcNow: () => poisonedTimestamp));

		AntigravityStatusLineAccountDisplay changed = new(
			"current@example.com",
			now);
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			changed,
			AntigravityPrivateKeyAcl.TryProtectNewFile,
			getUtcNow: () => now));
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			now,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal(changed, actual);
	}

	[Fact]
	public void TryWrite_AfterSmallClockRollback_ReplacesFutureDifferentAccount()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string capturePath = Path.Combine(
			temporaryDirectory.Path,
			"private",
			"antigravity-statusline",
			AntigravityStatusLineCapture.CaptureFileName);
		DateTimeOffset originalNow = CapturedAt;
		DateTimeOffset rolledBackNow = originalNow.AddMinutes(-1);
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("old@example.com", originalNow),
			AntigravityPrivateKeyAcl.TryProtectNewFile,
			getUtcNow: () => originalNow));
		List<string> notifications = new();
		AntigravityStatusLineAccountDisplay changed = new(
			"current@example.com",
			rolledBackNow);

		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			changed,
			AntigravityPrivateKeyAcl.TryProtectNewFile,
			notifications.Add,
			() => rolledBackNow));
		Assert.Equal(new[] { changed.Email }, notifications);
		Assert.True(AntigravityStatusLineCapture.TryRead(
			capturePath,
			rolledBackNow,
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Equal(changed, actual);
	}

	[Fact]
	public void TryRead_FarFutureCapture_FailsClosed()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string capturePath = Path.Combine(
			temporaryDirectory.Path,
			"private",
			"antigravity-statusline",
			AntigravityStatusLineCapture.CaptureFileName);
		DateTimeOffset now = CapturedAt;
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("future@example.com", now.AddMinutes(4)),
			AntigravityPrivateKeyAcl.TryProtectNewFile,
			getUtcNow: () => now));

		Assert.False(AntigravityStatusLineCapture.TryRead(
			capturePath,
			now.AddHours(-1),
			out AntigravityStatusLineAccountDisplay? actual));
		Assert.Null(actual);
	}

	[Fact]
	public void TryWrite_InputTimestampBeyondAllowedSkew_FailsWithoutMutation()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string capturePath = Path.Combine(
			temporaryDirectory.Path,
			"private",
			"antigravity-statusline",
			AntigravityStatusLineCapture.CaptureFileName);

		Assert.False(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new(
				"future@example.com",
				CapturedAt + AntigravityStatusLineCapture.MaximumFutureClockSkew +
					TimeSpan.FromSeconds(1)),
			AntigravityPrivateKeyAcl.TryProtectNewFile,
			getUtcNow: () => CapturedAt));
		Assert.False(File.Exists(capturePath));
	}
}
