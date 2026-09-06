using System.Security;
using System.Text.Json;

using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Persistence;

namespace AiUsageDashboard.Tests;

public sealed class AppDiagnosticsTests
{
	[Fact]
	public void GetAccountChangeFailureReason_WithDomainFailures_PreservesActionableMessage()
	{
		const string storeMessage = "帳號設定目前不可安全寫入。";
		const string stateMessage = "這張卡片正在連接帳號。";
		const string validationMessage = "帳號識別碼已存在。";

		Assert.Equal(
			storeMessage,
			FloatingWidgetWindow.GetAccountChangeFailureReason(
				new AccountProfileStoreException(storeMessage)));
		Assert.Equal(
			stateMessage,
			FloatingWidgetWindow.GetAccountChangeFailureReason(
				new InvalidOperationException(stateMessage)));
		Assert.Equal(
			validationMessage,
			FloatingWidgetWindow.GetAccountChangeFailureReason(
				new ArgumentException(
					validationMessage,
					"profile")));
	}

	[Fact]
	public void GetAccountChangeFailureReason_WithUnexpectedFailure_HidesRawMessage()
	{
		const string rawMessage = "sensitive synthetic detail";

		string reason = FloatingWidgetWindow.GetAccountChangeFailureReason(
			new Exception(rawMessage));

		Assert.Equal("更新帳號設定時發生錯誤。", reason);
		Assert.DoesNotContain(rawMessage, reason, StringComparison.Ordinal);
	}

	[Fact]
	public void GetDiagnosticFilePath_ReturnsApplicationScopedPath()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		string diagnosticPath = AppDiagnostics.GetDiagnosticFilePath();

		Assert.False(string.IsNullOrWhiteSpace(localApplicationData));
		Assert.True(Path.IsPathFullyQualified(diagnosticPath));
		Assert.Equal("diagnostics.log", Path.GetFileName(diagnosticPath));
		Assert.Equal(
			"AiUsageDashboard",
			Directory.GetParent(diagnosticPath)!.Name);
		Assert.Equal(
			Path.GetFullPath(localApplicationData),
			Directory.GetParent(diagnosticPath)!.Parent!.FullName);
	}

	[Fact]
	public void GetDiagnosticWriteLockFilePath_UsesPersistentDiagnosticSibling()
	{
		string diagnosticPath = AppDiagnostics.GetDiagnosticFilePath();

		string lockFilePath =
			AppDiagnostics.GetDiagnosticWriteLockFilePath(diagnosticPath);

		Assert.Equal($"{Path.GetFullPath(diagnosticPath)}.lock", lockFilePath);
		Assert.Equal(
			Path.GetDirectoryName(Path.GetFullPath(diagnosticPath)),
			Path.GetDirectoryName(lockFilePath));
	}

	[Fact]
	public void GetUserFacingFailureReason_UsesActionableCategoryFromInnerException()
	{
		Exception exception = new InvalidOperationException(
			"outer",
			new IOException("inner"));

		string reason = AppDiagnostics.GetUserFacingFailureReason(exception);

		Assert.Contains("讀取或寫入本機資料", reason, StringComparison.Ordinal);
		Assert.DoesNotContain("inner", reason, StringComparison.Ordinal);
	}

	[Fact]
	public void GetUserFacingFailureReason_WhenSettingsAreMalformed_ExplainsFormatProblem()
	{
		string reason = AppDiagnostics.GetUserFacingFailureReason(
			new JsonException("private-json-content"));

		Assert.Contains("格式無法讀取", reason, StringComparison.Ordinal);
		Assert.DoesNotContain("private-json-content", reason, StringComparison.Ordinal);
	}

	[Fact]
	public void GetUserFacingFailureReason_WithUnknownFailure_UsesCallerFallback()
	{
		const string expectedReason = "更新顯示偏好時發生未預期錯誤。";

		string reason = AppDiagnostics.GetUserFacingFailureReason(
			new InvalidOperationException("private-details"),
			expectedReason);

		Assert.Equal(expectedReason, reason);
		Assert.DoesNotContain("private-details", reason, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		(int)DashboardPreferencesSaveBlockReason.NewerSchema,
		"顯示設定由較新版本的 AI Usage 建立；為避免覆寫，這次變更沒有儲存。請使用相同或較新的 AI Usage 版本。")]
	[InlineData(
		(int)DashboardPreferencesSaveBlockReason.DocumentTooLarge,
		"顯示設定檔超過 64 KiB；為避免覆寫，這次變更沒有儲存。請先備份，再檢查或重建顯示設定檔。")]
	[InlineData(
		(int)DashboardPreferencesSaveBlockReason.ExistingSettingsUnavailable,
		"目前無法安全讀取原有顯示設定；為避免覆寫，這次變更沒有儲存。請稍後再試。")]
	[InlineData(
		(int)DashboardPreferencesSaveBlockReason.UnsafePath,
		"顯示設定檔路徑經過連結，或目標不是一般檔案；為保護本機資料，這次變更沒有儲存。請將 AI Usage 本機資料夾還原為一般資料夾與檔案後再試。")]
	public void GetShellPreferencesSaveFailureReason_WithBlockedSave_UsesTypedReason(
		int blockReasonValue,
		string expected)
	{
		DashboardPreferencesSaveBlockReason blockReason =
			(DashboardPreferencesSaveBlockReason)blockReasonValue;
		Exception exception = new DashboardPreferencesSaveBlockedException(
			blockReason,
			(blockReason ==
					DashboardPreferencesSaveBlockReason.ExistingSettingsUnavailable) ||
				(blockReason == DashboardPreferencesSaveBlockReason.UnsafePath)
				? new IOException("private-path-details")
				: null);

		string reason = AiUsageDashboard.App.App
			.GetShellPreferencesSaveFailureReason(exception);

		Assert.Equal(expected, reason);
		Assert.DoesNotContain("private-path-details", reason, StringComparison.Ordinal);
		Assert.DoesNotContain("可寫入", reason, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void GetShellPreferencesSaveFailureReason_WithPermissionFailure_UsesWritableGuidance(
		bool useSecurityException)
	{
		Exception innerException = useSecurityException
			? new SecurityException("private-security-details")
			: new UnauthorizedAccessException("private-permission-details");
		Exception exception = new DashboardPreferencesSaveBlockedException(
			DashboardPreferencesSaveBlockReason.UnsafePath,
			innerException);

		string reason = AiUsageDashboard.App.App
			.GetShellPreferencesSaveFailureReason(exception);

		Assert.Equal(
			"Windows 不允許存取 AI Usage 的本機資料。請確認 AI Usage 本機資料夾可讀取與寫入。",
			reason);
		Assert.DoesNotContain("private", reason, StringComparison.Ordinal);
	}

	[Fact]
	public void GetShellPreferencesSaveFailureReason_WithUnexpectedFailure_HidesRawReason()
	{
		const string RawReason = "private-unexpected-details";

		string reason = AiUsageDashboard.App.App
			.GetShellPreferencesSaveFailureReason(
				new InvalidOperationException(RawReason));

		Assert.Equal("儲存浮窗設定時發生未預期錯誤。", reason);
		Assert.DoesNotContain(RawReason, reason, StringComparison.Ordinal);
		Assert.DoesNotContain("可寫入", reason, StringComparison.Ordinal);
	}

	[Fact]
	public void ShellPreferencesSaveFailurePresentation_UsesSameReasonInTrayAndInline()
	{
		const string FailureReason = "已清理的顯示設定儲存失敗原因。";

		string notificationText = AiUsageDashboard.App.App
			.CreateShellPreferencesSaveFailureNotificationText(FailureReason);
		string inlineText = FloatingWidgetWindow
			.CreateShellPreferencesSaveFailureInlineText(FailureReason);

		Assert.Equal(
			$"{FailureReason} 本次仍可使用，重新啟動後可能恢復舊設定。",
			notificationText);
		Assert.Equal(
			$"無法儲存浮窗位置與顯示設定；{notificationText}",
			inlineText);
	}

	[Fact]
	public void TryWrite_WritesDiagnosticWithoutRawExceptionMessage()
	{
		string temporaryDirectory = CreateTemporaryDirectory();

		try
		{
			string diagnosticPath = Path.Combine(
				temporaryDirectory,
				"diagnostics.log");
			Exception exception = CaptureExceptionWithStackTrace();
			DateTimeOffset recordedAt = new(
				2026,
				7,
				26,
				10,
				30,
				0,
				TimeSpan.Zero);

			AppDiagnosticWriteResult result = AppDiagnostics.TryWrite(
				diagnosticPath,
				"startup",
				"本機設定無法讀取。",
				exception,
				recordedAt);

			Assert.True(result.WasWritten);
			Assert.Equal(diagnosticPath, result.FilePath);
			string lockFilePath =
				AppDiagnostics.GetDiagnosticWriteLockFilePath(diagnosticPath);
			Assert.True(File.Exists(lockFilePath));
			using FileStream reacquiredLock = new(
				lockFilePath,
				FileMode.Open,
				FileAccess.ReadWrite,
				FileShare.None);
			string content = File.ReadAllText(diagnosticPath);
			Assert.Contains(
				"[2026-07-26T10:30:00.0000000+00:00]",
				content,
				StringComparison.Ordinal);
			Assert.Contains("operation=startup", content, StringComparison.Ordinal);
			Assert.Contains(
				"exception_type=System.InvalidOperationException",
				content,
				StringComparison.Ordinal);
			Assert.Contains("stack_trace:", content, StringComparison.Ordinal);
			Assert.DoesNotContain(
				"secret-token=private",
				content,
				StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}

	[Fact]
	public void TryWrite_WhenPathCannotBeCreated_ReturnsFailure()
	{
		string temporaryDirectory = CreateTemporaryDirectory();

		try
		{
			string blockingFilePath = Path.Combine(
				temporaryDirectory,
				"not-a-directory");
			File.WriteAllText(blockingFilePath, "content");
			string diagnosticPath = Path.Combine(
				blockingFilePath,
				"diagnostics.log");

			AppDiagnosticWriteResult result = AppDiagnostics.TryWrite(
				diagnosticPath,
				"startup",
				"啟動失敗。",
				exception: null,
				DateTimeOffset.UtcNow);

			Assert.False(result.WasWritten);
			Assert.Equal(diagnosticPath, result.FilePath);
		}
		finally
		{
			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}

	[Fact]
	public void TryWrite_WhenDiagnosticLimitIsReached_RotatesOnePreviousFile()
	{
		string temporaryDirectory = CreateTemporaryDirectory();

		try
		{
			string diagnosticPath = Path.Combine(
				temporaryDirectory,
				"diagnostics.log");
			byte[] existingContent = new byte[512 * 1024];
			Array.Fill(existingContent, (byte)'x');
			File.WriteAllBytes(diagnosticPath, existingContent);

			AppDiagnosticWriteResult result = AppDiagnostics.TryWrite(
				diagnosticPath,
				"startup",
				"新的診斷摘要。",
				exception: null,
				DateTimeOffset.UtcNow);

			Assert.True(result.WasWritten);
			Assert.True(File.Exists($"{diagnosticPath}.1"));
			Assert.Equal(existingContent.Length, new FileInfo(
				$"{diagnosticPath}.1").Length);
			Assert.Contains(
				"summary=新的診斷摘要。",
				File.ReadAllText(diagnosticPath),
				StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}

	[Fact]
	public async Task TryWrite_WhenConcurrentWritersRotate_PreservesEveryEntry()
	{
		const int concurrentWriteCount = 32;
		string temporaryDirectory = CreateTemporaryDirectory();

		try
		{
			string diagnosticPath = Path.Combine(
				temporaryDirectory,
				"diagnostics.log");
			string lockFilePath =
				AppDiagnostics.GetDiagnosticWriteLockFilePath(diagnosticPath);
			byte[] existingContent = new byte[512 * 1024];
			Array.Fill(existingContent, (byte)'x');
			File.WriteAllBytes(diagnosticPath, existingContent);
			using ManualResetEventSlim startGate = new(initialState: false);
			Task<AppDiagnosticWriteResult>[] writeTasks = Enumerable.Range(
				0,
				concurrentWriteCount)
				.Select(index => Task.Factory.StartNew(
					() =>
					{
						startGate.Wait();
						return AppDiagnostics.TryWrite(
							diagnosticPath,
							"concurrent-rotate",
							$"entry-{index}",
							exception: null,
							DateTimeOffset.UtcNow,
							lockFilePath,
							TimeSpan.FromSeconds(10));
					},
					CancellationToken.None,
					TaskCreationOptions.LongRunning,
					TaskScheduler.Default))
				.ToArray();

			startGate.Set();
			AppDiagnosticWriteResult[] results = await Task.WhenAll(writeTasks)
				.WaitAsync(TimeSpan.FromSeconds(20));

			Assert.All(results, result => Assert.True(result.WasWritten));
			Assert.Equal(
				existingContent.Length,
				new FileInfo($"{diagnosticPath}.1").Length);
			string content = File.ReadAllText(diagnosticPath);
			string[] expectedSummaries = Enumerable.Range(
				0,
				concurrentWriteCount)
				.Select(index => $"summary=entry-{index}")
				.Order(StringComparer.Ordinal)
				.ToArray();
			string[] actualSummaries = content
				.Split(
					['\r', '\n'],
					StringSplitOptions.RemoveEmptyEntries)
				.Where(line => line.StartsWith(
					"summary=",
					StringComparison.Ordinal))
				.Order(StringComparer.Ordinal)
				.ToArray();

			Assert.Equal(expectedSummaries, actualSummaries);
		}
		finally
		{
			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}

	[Fact]
	public async Task TryWrite_WhenLockWaitTimesOut_ReturnsFailureWithoutWriting()
	{
		string temporaryDirectory = CreateTemporaryDirectory();

		try
		{
			string diagnosticPath = Path.Combine(
				temporaryDirectory,
				"diagnostics.log");
			string lockFilePath =
				AppDiagnostics.GetDiagnosticWriteLockFilePath(diagnosticPath);
			using FileStream ownerLock = new(
				lockFilePath,
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.None);
			Task<AppDiagnosticWriteResult> writeTask = Task.Run(() =>
				AppDiagnostics.TryWrite(
					diagnosticPath,
					"lock-timeout",
					"should-not-be-written",
					exception: null,
					DateTimeOffset.UtcNow,
					lockFilePath,
					TimeSpan.FromMilliseconds(50)));

			AppDiagnosticWriteResult result = await writeTask.WaitAsync(
				TimeSpan.FromSeconds(5));

			Assert.False(result.WasWritten);
			Assert.False(File.Exists(diagnosticPath));
		}
		finally
		{
			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}

	[Fact]
	public async Task TryWrite_WhenAnotherProcessOwnsLock_TimesOutThenRecovers()
	{
		const string lockOwnerScript = """
			$ErrorActionPreference = 'Stop'
			$lockPath = [Console]::In.ReadLine()
			$stream = [System.IO.File]::Open(
				$lockPath,
				[System.IO.FileMode]::OpenOrCreate,
				[System.IO.FileAccess]::ReadWrite,
				[System.IO.FileShare]::None)
			try {
				[Console]::Out.WriteLine('READY')
				[Console]::Out.Flush()
				[Console]::In.ReadLine() | Out-Null
			}
			finally {
				$stream.Dispose()
			}
			""";
		string temporaryDirectory = CreateTemporaryDirectory();
		string powerShellPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.System),
			"WindowsPowerShell",
			"v1.0",
			"powershell.exe");
		string diagnosticPath = Path.Combine(
			temporaryDirectory,
			"diagnostics.log");
		string lockFilePath =
			AppDiagnostics.GetDiagnosticWriteLockFilePath(diagnosticPath);
		System.Diagnostics.ProcessStartInfo startInfo = new(powerShellPath)
		{
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("-NoLogo");
		startInfo.ArgumentList.Add("-NoProfile");
		startInfo.ArgumentList.Add("-NonInteractive");
		startInfo.ArgumentList.Add("-Command");
		startInfo.ArgumentList.Add(lockOwnerScript);
		using System.Diagnostics.Process lockOwner = new()
		{
			StartInfo = startInfo
		};
		bool isLockOwnerStarted = false;

		try
		{
			Assert.True(File.Exists(powerShellPath));
			isLockOwnerStarted = lockOwner.Start();
			Assert.True(isLockOwnerStarted);
			await lockOwner.StandardInput.WriteLineAsync(lockFilePath);
			await lockOwner.StandardInput.FlushAsync();
			string? ready = await lockOwner.StandardOutput
				.ReadLineAsync()
				.WaitAsync(TimeSpan.FromSeconds(30));
			Assert.Equal("READY", ready);

			AppDiagnosticWriteResult blocked = AppDiagnostics.TryWrite(
				diagnosticPath,
				"cross-process-lock",
				"blocked-write",
				exception: null,
				DateTimeOffset.UtcNow);

			Assert.False(blocked.WasWritten);
			Assert.False(File.Exists(diagnosticPath));

			await lockOwner.StandardInput.WriteLineAsync("release");
			lockOwner.StandardInput.Close();
			await lockOwner.WaitForExitAsync()
				.WaitAsync(TimeSpan.FromSeconds(30));
			Assert.Equal(0, lockOwner.ExitCode);
			Assert.Equal(
				string.Empty,
				await lockOwner.StandardError.ReadToEndAsync());

			AppDiagnosticWriteResult recovered = AppDiagnostics.TryWrite(
				diagnosticPath,
				"cross-process-lock",
				"recovered-write",
				exception: null,
				DateTimeOffset.UtcNow);

			Assert.True(recovered.WasWritten);
			Assert.Contains(
				"summary=recovered-write",
				File.ReadAllText(diagnosticPath),
				StringComparison.Ordinal);
		}
		finally
		{
			if (isLockOwnerStarted && !lockOwner.HasExited)
			{
				lockOwner.Kill(entireProcessTree: true);
				await lockOwner.WaitForExitAsync();
			}

			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}

	[Fact]
	public void CreateStartupFailureMessage_IncludesReasonAndDiagnosticPath()
	{
		const string diagnosticPath =
			@"C:\Users\test\AppData\Local\AiUsageDashboard\diagnostics.log";
		Exception exception =
			new UnauthorizedAccessException("private-path-details");

		string message = AiUsageDashboard.App.App.CreateStartupFailureMessage(
			exception,
			new AppDiagnosticWriteResult(
				diagnosticPath,
				WasWritten: true));

		Assert.Contains(
			"Windows 不允許存取 AI Usage 的本機資料",
			message,
			StringComparison.Ordinal);
		Assert.Contains(diagnosticPath, message, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"帳號設定檔不會被覆寫",
			message,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"private-path-details",
			message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void CreateActivationFailureMessage_IncludesRecoveryAndDiagnosticFallback()
	{
		string message = AiUsageDashboard.App.App.CreateActivationFailureMessage(
			SingleInstanceActivationFailure.TimedOut,
			new AppDiagnosticWriteResult(
				FilePath: null,
				WasWritten: false));

		Assert.Contains("系統匣", message, StringComparison.Ordinal);
		Assert.Contains("顯示浮窗", message, StringComparison.Ordinal);
		Assert.Contains("工作管理員", message, StringComparison.Ordinal);
		Assert.Contains("沒有在預期時間內回應", message, StringComparison.Ordinal);
		Assert.Contains("診斷紀錄也無法建立", message, StringComparison.Ordinal);
	}

	private static Exception CaptureExceptionWithStackTrace()
	{
		try
		{
			throw new InvalidOperationException("secret-token=private");
		}
		catch (Exception exception)
		{
			return exception;
		}
	}

	private static string CreateTemporaryDirectory()
	{
		string directoryPath = Path.Combine(
			Path.GetTempPath(),
			$"AiUsageDashboard.Tests.{Guid.NewGuid():N}");
		Directory.CreateDirectory(directoryPath);
		return directoryPath;
	}
}
