using System.ComponentModel;
using System.Diagnostics;

namespace AiUsageDashboard.Updater;

internal sealed record ExactProcessIdentity(
	int ProcessId,
	long ProcessStartTimeUtcTicks);

internal interface IExactProcessExitWaiter
{
	Task WaitForExitAsync(
		ExactProcessIdentity identity,
		TimeSpan timeout,
		CancellationToken cancellationToken);
}

internal sealed class SystemExactProcessExitWaiter : IExactProcessExitWaiter
{
	public async Task WaitForExitAsync(
		ExactProcessIdentity identity,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(identity);

		if ((identity.ProcessId <= 0) ||
			(identity.ProcessStartTimeUtcTicks <= 0))
		{
			throw new ArgumentException(
				"The process identity must contain a positive PID and start time.",
				nameof(identity));
		}

		if ((timeout <= TimeSpan.Zero) &&
			(timeout != Timeout.InfiniteTimeSpan))
		{
			throw new ArgumentOutOfRangeException(nameof(timeout));
		}

		Process process;

		try
		{
			process = Process.GetProcessById(identity.ProcessId);
		}
		catch (ArgumentException)
		{
			return;
		}

		using (process)
		{
			long observedStartTimeUtcTicks;

			try
			{
				_ = process.SafeHandle;
				observedStartTimeUtcTicks =
					process.StartTime.ToUniversalTime().Ticks;
			}
			catch (InvalidOperationException exception)
			{
				if (TryProveExactProcessExited(identity))
				{
					return;
				}

				throw new InvalidOperationException(
					$"Promotion parent process {identity.ProcessId} remained active " +
					"after its exact identity handle became unavailable.",
					exception);
			}
			catch (Win32Exception exception)
			{
				throw new InvalidOperationException(
					$"Unable to inspect promotion parent process " +
					$"{identity.ProcessId}.",
					exception);
			}

			if (observedStartTimeUtcTicks !=
				identity.ProcessStartTimeUtcTicks)
			{
				return;
			}

			try
			{
				await process.WaitForExitAsync(cancellationToken)
					.WaitAsync(timeout, cancellationToken);
			}
			catch (TimeoutException exception)
			{
				throw new TimeoutException(
					$"Promotion parent process {identity.ProcessId} did not exit " +
					$"within {timeout}.",
					exception);
			}
			catch (InvalidOperationException exception)
			{
				if (!TryProveExactProcessExited(identity))
				{
					throw new InvalidOperationException(
						$"Promotion parent process {identity.ProcessId} remained " +
						"active after waiting for its exact handle failed.",
						exception);
				}
			}
			catch (Win32Exception exception)
			{
				throw new InvalidOperationException(
					$"Unable to wait for promotion parent process " +
					$"{identity.ProcessId} to exit.",
					exception);
			}
		}
	}

	private static bool TryProveExactProcessExited(ExactProcessIdentity identity)
	{
		try
		{
			using Process candidate = Process.GetProcessById(identity.ProcessId);
			_ = candidate.SafeHandle;
			return candidate.StartTime.ToUniversalTime().Ticks !=
				identity.ProcessStartTimeUtcTicks;
		}
		catch (ArgumentException)
		{
			return true;
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or Win32Exception)
		{
			throw new InvalidOperationException(
				$"Unable to prove promotion parent process " +
				$"{identity.ProcessId} exited.",
				exception);
		}
	}
}
