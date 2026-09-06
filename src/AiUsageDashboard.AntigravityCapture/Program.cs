using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.Licensing;

namespace AiUsageDashboard.AntigravityCapture;

internal static class Program
{
	private static readonly TimeSpan StandardInputTimeout =
		TimeSpan.FromSeconds(2);

	private static async Task<int> Main(string[] args)
	{
		if (LegalCommandLine.IsLegalCommand(args))
		{
			try
			{
				LegalCommandLine.TryHandle(args, LegalCatalog.Load(LegalProfile.Capture),
					LegalAcceptanceStore.CreateDefault, out int legalExitCode);
				return legalExitCode;
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine($"License command failed: {exception.Message}");
				return LegalCommandLine.FailureExitCode;
			}
		}

		if ((args.Length == 1) &&
			string.Equals(
				args[0],
				AntigravityStatusLineCapture.SmokeTestArgument,
				StringComparison.Ordinal))
		{
			return RunSmokeTest();
		}

		byte[] payload = new byte[
			AntigravityStatusLineCapture.MaximumPayloadBytes + 1];

		try
		{
			if ((args.Length != 1) ||
				!string.Equals(
					args[0],
					AntigravityStatusLineCapture.MarkerArgument,
					StringComparison.Ordinal))
			{
				return 0;
			}

			using Stream input = Console.OpenStandardInput();
			using CancellationTokenSource inputTimeout = new(
				StandardInputTimeout);
			Task timeoutTask = Task.Delay(
				Timeout.InfiniteTimeSpan,
				inputTimeout.Token);
			int length = 0;

			while (length < payload.Length)
			{
				ValueTask<int> pendingRead = input.ReadAsync(
					payload.AsMemory(length, payload.Length - length),
					inputTimeout.Token);
				int read;

				if (pendingRead.IsCompletedSuccessfully)
				{
					read = pendingRead.Result;
				}
				else
				{
					Task<int> readTask = pendingRead.AsTask();
					Task completed = await Task.WhenAny(readTask, timeoutTask);

					if ((completed != readTask) && !readTask.IsCompleted)
					{
						ObserveFault(readTask);
						return 0;
					}

					read = await readTask;
				}

				if (read == 0)
				{
					break;
				}

				length += read;
			}

			if ((length == 0) ||
				(length > AntigravityStatusLineCapture.MaximumPayloadBytes))
			{
				return 0;
			}

			if (AntigravityStatusLineCapture.TryParsePayload(
					payload.AsSpan(0, length),
					DateTimeOffset.UtcNow,
					out AntigravityStatusLineAccountDisplay? accountDisplay))
			{
				int legalExitCode = LegalCallbackGate.CheckAcceptance(
					() => LegalCatalog.Load(LegalProfile.Capture), LegalAcceptanceStore.CreateDefault);
				if (legalExitCode != 0)
				{
					return legalExitCode;
				}

				_ = AntigravityStatusLineCapture.TryWrite(accountDisplay!);
			}
		}
		catch
		{
			// AGY's status line must never be disrupted or receive sensitive
			// payload/path details through this helper's output streams.
		}
		finally
		{
			CryptographicOperations.ZeroMemory(payload);
		}

		return 0;
	}

	private static int RunSmokeTest()
	{
		const string ExpectedEmail = "self-test@example.invalid";
		const string ExpectedPlanTier = "Pro";
		byte[] payload = Encoding.UTF8.GetBytes(
			$"{{\"email\":\"{ExpectedEmail}\",\"plan_tier\":\"{ExpectedPlanTier}\"}}");
		string smokeRoot = Path.Combine(
			Path.GetTempPath(),
			$"AiUsageDashboard.AntigravityCapture.Smoke.{Guid.NewGuid():N}");
		bool succeeded = false;
		bool cleanupSucceeded = true;

		try
		{
			string applicationPath = Path.Combine(
				smokeRoot,
				"AiUsageDashboard");
			Directory.CreateDirectory(applicationPath);
			string capturePath = Path.Combine(
				applicationPath,
				"private",
				"antigravity-statusline",
				AntigravityStatusLineCapture.CaptureFileName);

			succeeded =
				AntigravityStatusLineCapture.TryParsePayload(
					payload,
					DateTimeOffset.UnixEpoch,
					out AntigravityStatusLineAccountDisplay? accountDisplay) &&
				(accountDisplay is not null) &&
				string.Equals(
					accountDisplay.Email,
					ExpectedEmail,
					StringComparison.Ordinal) &&
				string.Equals(
					accountDisplay.PlanTier,
					ExpectedPlanTier,
					StringComparison.Ordinal) &&
				(accountDisplay.CapturedAtUtc == DateTimeOffset.UnixEpoch) &&
				AntigravityStatusLineCapture.TryWrite(
					capturePath,
					accountDisplay) &&
				AntigravityStatusLineCapture.TryRead(
					capturePath,
					out AntigravityStatusLineAccountDisplay? saved) &&
				(saved == accountDisplay);
		}
		catch
		{
			succeeded = false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(payload);

			try
			{
				if (Directory.Exists(smokeRoot))
				{
					Directory.Delete(smokeRoot, recursive: true);
				}
			}
			catch
			{
				cleanupSucceeded = false;
			}
		}

		return succeeded && cleanupSucceeded ? 0 : 1;
	}

	private static void ObserveFault(Task task)
	{
		_ = task.ContinueWith(
			static completed => _ = completed.Exception,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously |
				TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}
}
