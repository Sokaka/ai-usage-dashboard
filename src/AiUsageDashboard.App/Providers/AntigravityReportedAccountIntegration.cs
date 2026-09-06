using System.IO;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal sealed class AntigravityReportedAccountSource :
	IAntigravityReportedAccountSource,
	IDisposable
{
	private readonly record struct SourceReadResult(
		AntigravityReportedAccountObservationStatus Status,
		AntigravityReportedAccount? Account);

	private readonly object _observationLock = new();
	private readonly string _packagedHelperPath;
	private long _observationGeneration;
	private bool _hasObservation;
	private AntigravityReportedAccount? _lastObservedAccount;
	private string? _observationSignalEmail;
	private AntigravityStatusLineObservationSignal.Listener? _observationSignal;

	internal AntigravityReportedAccountSource(string packagedHelperPath)
	{
		_packagedHelperPath = packagedHelperPath ??
			throw new ArgumentNullException(nameof(packagedHelperPath));
	}

	public async ValueTask<AntigravityReportedAccountObservation> ReadAsync(
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		SourceReadResult readResult;

		try
		{
			readResult = await Task.Run(
				ReadSource,
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			readResult = new SourceReadResult(
				AntigravityReportedAccountObservationStatus.Unavailable,
				null);
		}

		lock (_observationLock)
		{
			if (readResult.Status ==
				AntigravityReportedAccountObservationStatus.Unavailable)
			{
				// A transient settings/capture failure is not a new observation.
				// Preserve the listener and generation so a later successful read
				// can still prove that the status line ran during a quota refresh.
				return new AntigravityReportedAccountObservation(
					_observationGeneration,
					null,
					HasFreshStatusLineInvocation: false,
					Status:
						AntigravityReportedAccountObservationStatus.Unavailable);
			}

			AntigravityReportedAccount? account = readResult.Account;
			bool hasFreshStatusLineInvocation = false;

			if (!string.Equals(
					_observationSignalEmail,
					account?.Email,
					StringComparison.OrdinalIgnoreCase))
			{
				_observationSignal?.Dispose();
				_observationSignal = account is null
					? null
					: AntigravityStatusLineObservationSignal.Listener.TryCreate(
						account.Email);
				_observationSignalEmail = _observationSignal is null
					? null
					: account?.Email;
			}
			else if ((account is not null) &&
				(_observationSignal?.TryConsume() == true))
			{
				hasFreshStatusLineInvocation = true;
			}

			if (!_hasObservation ||
				!Equals(_lastObservedAccount, account) ||
				hasFreshStatusLineInvocation)
			{
				_hasObservation = true;
				_lastObservedAccount = account;

				if (_observationGeneration < long.MaxValue)
				{
					_observationGeneration++;
				}
			}

			return new AntigravityReportedAccountObservation(
				_observationGeneration,
				account,
				hasFreshStatusLineInvocation,
				AntigravityReportedAccountObservationStatus.Available);
		}
	}

	private SourceReadResult ReadSource()
	{
		AntigravityStatusLineIntegrationResult integration =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				_packagedHelperPath);

		if (!integration.IsOwnedConfiguration)
		{
			return GetUnownedIntegrationObservationStatus(integration.Status) ==
				AntigravityReportedAccountObservationStatus.Available
					? new SourceReadResult(
						AntigravityReportedAccountObservationStatus.Available,
						null)
					: new SourceReadResult(
						AntigravityReportedAccountObservationStatus.Unavailable,
						null);
		}

		if (!AntigravityStatusLineIntegration.IsOwnedStatusLineConfigured())
		{
			// The boolean verifier also represents settings read failures. Do not
			// collapse those transient failures into affirmative missing evidence.
			return new SourceReadResult(
				AntigravityReportedAccountObservationStatus.Unavailable,
				null);
		}

		string captureFilePath =
			AntigravityStatusLineCapture.GetCaptureFilePath();
		if (!TryGetCaptureFilePresence(
			captureFilePath,
			out bool captureFileExists))
		{
			return new SourceReadResult(
				AntigravityReportedAccountObservationStatus.Unavailable,
				null);
		}

		if (!captureFileExists)
		{
			return new SourceReadResult(
				AntigravityReportedAccountObservationStatus.Available,
				null);
		}

		if (!AntigravityStatusLineCapture.TryRead(
				out AntigravityStatusLineAccountDisplay? display) ||
			(display is null))
		{
			return new SourceReadResult(
				AntigravityReportedAccountObservationStatus.Unavailable,
				null);
		}

		return new SourceReadResult(
			AntigravityReportedAccountObservationStatus.Available,
			new AntigravityReportedAccount(
				display.Email,
				display.CapturedAtUtc,
				display.PlanTier));
	}

	internal static AntigravityReportedAccountObservationStatus
		GetUnownedIntegrationObservationStatus(
			AntigravityStatusLineIntegrationStatus status)
	{
		return status ==
			AntigravityStatusLineIntegrationStatus.ExistingCustomStatusLine
				? AntigravityReportedAccountObservationStatus.Available
				: AntigravityReportedAccountObservationStatus.Unavailable;
	}

	private static bool TryGetCaptureFilePresence(
		string captureFilePath,
		out bool exists)
	{
		exists = false;

		try
		{
			_ = File.GetAttributes(captureFilePath);
			exists = true;
			return true;
		}
		catch (FileNotFoundException)
		{
			return true;
		}
		catch (DirectoryNotFoundException)
		{
			return true;
		}
		catch
		{
			return false;
		}
	}

	public async ValueTask<bool> RemoveOwnedAsync(
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return await Task.Run(
			AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine,
			cancellationToken).ConfigureAwait(false);
	}

	public void Dispose()
	{
		lock (_observationLock)
		{
			_observationSignal?.Dispose();
			_observationSignal = null;
		}
	}
}
