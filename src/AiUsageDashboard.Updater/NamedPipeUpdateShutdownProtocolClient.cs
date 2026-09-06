using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.Updater;

internal sealed class NamedPipeUpdateShutdownProtocolClient :
	IUpdateShutdownProtocolClient
{
	private readonly string _pipeName;

	internal NamedPipeUpdateShutdownProtocolClient(string pipeName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
		_pipeName = pipeName;
	}

	public async Task<UpdateShutdownQueryResult> QueryAsync(
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		UpdateShutdownClientResult result =
			await UpdateShutdownChannel.RequestAsync(
				_pipeName,
				UpdateShutdownRequest.CreateQuery(Guid.NewGuid()),
				timeout,
				cancellationToken);
		UpdateShutdownResponse? response = result.Response;

		if ((result.Failure != UpdateShutdownClientFailure.None) ||
			(response is null) ||
			(response.Outcome != UpdateShutdownOutcome.Observed) ||
			(response.TargetIdentity is not UpdateProcessIdentity identity))
		{
			return new UpdateShutdownQueryResult(
				IsObserved: false,
				Identity: null,
				FormatFailure(result));
		}

		return new UpdateShutdownQueryResult(
			IsObserved: true,
				ToObservedIdentity(identity),
				Failure: null);
	}

	public async Task<UpdateShutdownReservationResult> ReserveAsync(
		ObservedUpdateProcessIdentity identity,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(identity);
		UpdateProcessIdentity expectedIdentity = new(
			identity.ProcessId,
			identity.ProcessStartTimeUtcTicks,
			identity.PayloadGenerationId);
		UpdateShutdownClientResult result =
			await UpdateShutdownChannel.RequestAsync(
				_pipeName,
				UpdateShutdownRequest.CreateReserve(
					Guid.NewGuid(),
					expectedIdentity),
				timeout,
				cancellationToken);
		UpdateShutdownResponse? response = result.Response;
		ObservedUpdateProcessIdentity? observedIdentity =
			response?.TargetIdentity is UpdateProcessIdentity targetIdentity
				? ToObservedIdentity(targetIdentity)
				: null;

		return new UpdateShutdownReservationResult(
			IsAccepted:
				(result.Failure == UpdateShutdownClientFailure.None) &&
				(response?.Outcome == UpdateShutdownOutcome.Accepted),
			observedIdentity,
			(result.Failure == UpdateShutdownClientFailure.None) &&
				(response is not null)
					? response.Outcome.ToString()
					: result.Failure.ToString());
	}

	private static string FormatFailure(UpdateShutdownClientResult result)
	{
		return (result.Failure == UpdateShutdownClientFailure.None) &&
			(result.Response is not null)
			? result.Response.Outcome.ToString()
			: result.Failure.ToString();
	}

	private static ObservedUpdateProcessIdentity ToObservedIdentity(
		UpdateProcessIdentity identity)
	{
		return new ObservedUpdateProcessIdentity(
			identity.ProcessId,
			identity.ProcessStartTimeUtcTicks,
			identity.PayloadGenerationId);
	}
}
