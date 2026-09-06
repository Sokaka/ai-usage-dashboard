using System.Diagnostics;

namespace AiUsageDashboard.Updater.Core;

internal readonly record struct UpdateProcessIdentity
{
	internal const int MaximumPayloadGenerationIdLength = 64;

	internal int ProcessId { get; }
	internal long ProcessStartTimeUtcTicks { get; }
	internal string PayloadGenerationId { get; }

	internal UpdateProcessIdentity(
		int processId,
		long processStartTimeUtcTicks,
		string payloadGenerationId)
	{
		if (processId <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(processId));
		}

		if ((processStartTimeUtcTicks <= DateTime.MinValue.Ticks) ||
			(processStartTimeUtcTicks > DateTime.MaxValue.Ticks))
		{
			throw new ArgumentOutOfRangeException(nameof(processStartTimeUtcTicks));
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(payloadGenerationId);

		if ((payloadGenerationId.Length > MaximumPayloadGenerationIdLength) ||
			!payloadGenerationId.All(IsGenerationIdCharacter))
		{
			throw new ArgumentException(
				"Payload generation ID 格式無效。",
				nameof(payloadGenerationId));
		}

		ProcessId = processId;
		ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
		PayloadGenerationId = payloadGenerationId;
	}

	internal static UpdateProcessIdentity CaptureCurrent(
		string payloadGenerationId)
	{
		using Process process = Process.GetCurrentProcess();
		return new UpdateProcessIdentity(
			process.Id,
			process.StartTime.ToUniversalTime().Ticks,
			payloadGenerationId);
	}

	private static bool IsGenerationIdCharacter(char value)
	{
		return char.IsAsciiLetterOrDigit(value) ||
			(value == '.') ||
			(value == '-') ||
			(value == '_');
	}
}
