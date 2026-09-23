namespace AiUsageDashboard.AntigravitySpike;

internal sealed class ConPtyAntigravityCliVersionProbe : IAntigravityCliVersionProbe
{
	private const int MaximumOutputBytes = 64 * 1024;
	private static readonly TimeSpan CommandTimeout =
		AntigravityOfficialPrintTiming.CapabilityCommandTimeout;
	private static readonly TimeSpan ProcessCleanupTimeout =
		AntigravityOfficialPrintTiming.CapabilityCleanupTimeout;

	public async Task<string> ProbeAsync(
		string absolutePath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(absolutePath) ||
			!Path.IsPathFullyQualified(absolutePath))
		{
			throw CreateFailure(
				AntigravityCliVersionProbeFailureReason.Unexpected);
		}

		string? workingDirectory = Path.GetDirectoryName(absolutePath);
		if (string.IsNullOrWhiteSpace(workingDirectory))
		{
			throw CreateFailure(
				AntigravityCliVersionProbeFailureReason.Unexpected);
		}

		ConPtyStartRequest request;
		try
		{
			request = new ConPtyStartRequest(
				absolutePath,
				new[] { "--version" },
				workingDirectory,
				AntigravityCliProcessEnvironment.BuildAllowlist(),
				80,
				25,
				MaximumOutputBytes,
				ProcessCleanupTimeout);
		}
		catch (Exception exception)
		{
			throw CreateFailure(
				AntigravityCliVersionProbeFailureReason.StartFailed,
				exception);
		}

		using CancellationTokenSource timeoutSource = new(CommandTimeout);
		using CancellationTokenSource linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutSource.Token);
		WindowsConPtySession? session = null;
		bool isDisposed = false;

		try
		{
			try
			{
				session = await WindowsConPtySession.StartAsync(
					request,
					linkedSource.Token);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception)
			{
				throw CreateFailure(
					AntigravityCliVersionProbeFailureReason.StartFailed,
					exception);
			}

			int exitCode;
			try
			{
				exitCode = await session.WaitForExitAsync(linkedSource.Token);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception)
			{
				throw CreateFailure(
					AntigravityCliVersionProbeFailureReason.WaitFailed,
					exception);
			}

			await session.DisposeAsync();
			isDisposed = true;
			cancellationToken.ThrowIfCancellationRequested();
			ConPtyOutputSnapshot output = session.GetOutputSnapshot();
			if ((output.ReadFailure is not null) ||
				(output.TotalBytesRead < output.SavedBytes.Length))
			{
				throw CreateFailure(
					AntigravityCliVersionProbeFailureReason.OutputReadFailed,
					output.ReadFailure);
			}

			if (output.IsSavedByteLimitExceeded ||
				(output.TotalBytesRead > MaximumOutputBytes) ||
				(output.SavedBytes.Length > MaximumOutputBytes))
			{
				throw CreateFailure(
					AntigravityCliVersionProbeFailureReason.OutputLimitExceeded);
			}

			if (exitCode != 0)
			{
				throw CreateFailure(
					AntigravityCliVersionProbeFailureReason.NonZeroExit);
			}

			return AntigravityCliVersionOutputRenderer.Render(
				output.SavedBytes.Span);
		}
		catch (OperationCanceledException) when (
			linkedSource.IsCancellationRequested)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			throw CreateFailure(
				AntigravityCliVersionProbeFailureReason.TimedOut);
		}
		finally
		{
			if ((session is not null) && !isDisposed)
			{
				await session.DisposeAsync();
			}

			if (session is not null)
			{
				await session.QuiescenceTask;
			}
		}
	}

	private static AntigravityCliVersionProbeException CreateFailure(
		AntigravityCliVersionProbeFailureReason reason,
		Exception? innerException = null)
	{
		return new AntigravityCliVersionProbeException(reason, innerException);
	}
}
