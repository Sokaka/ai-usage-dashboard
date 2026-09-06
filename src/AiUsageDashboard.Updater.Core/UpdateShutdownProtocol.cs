using System.Buffers.Binary;
using System.Text;

namespace AiUsageDashboard.Updater.Core;

internal enum UpdateShutdownOperation : byte
{
	Query = 1,
	Reserve = 2
}

internal enum UpdateShutdownOutcome : byte
{
	Observed = 1,
	Accepted = 2,
	Busy = 3,
	IdentityMismatch = 4,
	AlreadyShuttingDown = 5,
	UnsupportedProtocol = 6,
	MalformedRequest = 7
}

internal sealed record UpdateShutdownRequest
{
	internal Guid RequestId { get; }
	internal UpdateShutdownOperation Operation { get; }
	internal UpdateProcessIdentity? ExpectedIdentity { get; }

	private UpdateShutdownRequest(
		Guid requestId,
		UpdateShutdownOperation operation,
		UpdateProcessIdentity? expectedIdentity)
	{
		if (requestId == Guid.Empty)
		{
			throw new ArgumentException(
				"Update shutdown request ID 不可為空。",
				nameof(requestId));
		}

		RequestId = requestId;
		Operation = operation;
		ExpectedIdentity = expectedIdentity;
	}

	internal static UpdateShutdownRequest CreateQuery(Guid requestId)
	{
		return new UpdateShutdownRequest(
			requestId,
			UpdateShutdownOperation.Query,
			expectedIdentity: null);
	}

	internal static UpdateShutdownRequest CreateReserve(
		Guid requestId,
		UpdateProcessIdentity expectedIdentity)
	{
		return new UpdateShutdownRequest(
			requestId,
			UpdateShutdownOperation.Reserve,
			expectedIdentity);
	}
}

internal sealed record UpdateShutdownResponse
{
	internal Guid RequestId { get; }
	internal UpdateShutdownOutcome Outcome { get; }
	internal UpdateProcessIdentity? TargetIdentity { get; }

	internal UpdateShutdownResponse(
		Guid requestId,
		UpdateShutdownOutcome outcome,
		UpdateProcessIdentity? targetIdentity)
	{
		if (!Enum.IsDefined(outcome))
		{
			throw new ArgumentOutOfRangeException(nameof(outcome));
		}

		RequestId = requestId;
		Outcome = outcome;
		TargetIdentity = targetIdentity;
	}
}

internal static class UpdateShutdownProtocol
{
	internal const int FrameSize = 128;
	internal const byte CurrentVersion = 1;

	private const int FlagsOffset = 7;
	private const int FrameKindOffset = 5;
	private const int FrameLengthOffset = 8;
	private const int GenerationIdLengthOffset = 40;
	private const int GenerationIdOffset = 42;
	private const int MagicOffset = 0;
	private const int OperationOrOutcomeOffset = 6;
	private const int ProcessIdOffset = 28;
	private const int ProcessStartTimeOffset = 32;
	private const int RequestIdOffset = 12;
	private const int ReservedOffset = GenerationIdOffset +
		UpdateProcessIdentity.MaximumPayloadGenerationIdLength;
	private const int VersionOffset = 4;
	private const byte RequestFrameKind = 1;
	private const byte ResponseFrameKind = 2;
	private static ReadOnlySpan<byte> Magic => "AUDU"u8;

	internal static byte[] EncodeRequest(UpdateShutdownRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		byte[] frame = CreateFrame(
			RequestFrameKind,
			(byte)request.Operation,
			request.RequestId);

		if (request.Operation == UpdateShutdownOperation.Reserve)
		{
			WriteIdentity(
				frame,
				request.ExpectedIdentity ?? throw new InvalidOperationException(
					"Reserve request 缺少 process identity。"));
		}

		return frame;
	}

	internal static byte[] EncodeResponse(UpdateShutdownResponse response)
	{
		ArgumentNullException.ThrowIfNull(response);
		byte[] frame = CreateFrame(
			ResponseFrameKind,
			(byte)response.Outcome,
			response.RequestId);

		if (response.TargetIdentity is UpdateProcessIdentity targetIdentity)
		{
			WriteIdentity(frame, targetIdentity);
		}

		return frame;
	}

	internal static bool TryDecodeRequest(
		ReadOnlySpan<byte> frame,
		out UpdateShutdownRequest? request,
		out Guid requestId,
		out UpdateShutdownOutcome rejectionOutcome)
	{
		request = null;
		requestId = TryReadRequestId(frame);
		rejectionOutcome = UpdateShutdownOutcome.MalformedRequest;

		if (!HasValidEnvelope(frame, RequestFrameKind))
		{
			return false;
		}

		requestId = new Guid(frame.Slice(RequestIdOffset, 16));

		if (frame[VersionOffset] != CurrentVersion)
		{
			rejectionOutcome = UpdateShutdownOutcome.UnsupportedProtocol;
			return false;
		}

		if (requestId == Guid.Empty)
		{
			return false;
		}

		try
		{
			switch ((UpdateShutdownOperation)frame[OperationOrOutcomeOffset])
			{
				case UpdateShutdownOperation.Query:
					if (!HasEmptyIdentity(frame))
					{
						return false;
					}

					request = UpdateShutdownRequest.CreateQuery(requestId);
					return true;
				case UpdateShutdownOperation.Reserve:
					if (!TryReadIdentity(
							frame,
							out UpdateProcessIdentity identity))
					{
						return false;
					}

					request = UpdateShutdownRequest.CreateReserve(
						requestId,
						identity);
					return true;
				default:
					return false;
			}
		}
		catch (Exception exception) when (
			exception is ArgumentException or DecoderFallbackException)
		{
			return false;
		}
	}

	internal static bool TryDecodeResponse(
		ReadOnlySpan<byte> frame,
		out UpdateShutdownResponse? response)
	{
		response = null;

		if (!HasValidEnvelope(frame, ResponseFrameKind) ||
			(frame[VersionOffset] != CurrentVersion))
		{
			return false;
		}

		Guid requestId = new(frame.Slice(RequestIdOffset, 16));
		UpdateShutdownOutcome outcome =
			(UpdateShutdownOutcome)frame[OperationOrOutcomeOffset];

		if ((requestId == Guid.Empty) || !Enum.IsDefined(outcome))
		{
			return false;
		}

		try
		{
			UpdateProcessIdentity? identity = null;

			if (!HasEmptyIdentity(frame))
			{
				if (!TryReadIdentity(frame, out UpdateProcessIdentity parsedIdentity))
				{
					return false;
				}

				identity = parsedIdentity;
			}

			if (RequiresIdentity(outcome) && !identity.HasValue)
			{
				return false;
			}

			response = new UpdateShutdownResponse(requestId, outcome, identity);
			return true;
		}
		catch (Exception exception) when (
			exception is ArgumentException or DecoderFallbackException)
		{
			return false;
		}
	}

	private static byte[] CreateFrame(
		byte frameKind,
		byte operationOrOutcome,
		Guid requestId)
	{
		byte[] frame = new byte[FrameSize];
		Magic.CopyTo(frame.AsSpan(MagicOffset, Magic.Length));
		frame[VersionOffset] = CurrentVersion;
		frame[FrameKindOffset] = frameKind;
		frame[OperationOrOutcomeOffset] = operationOrOutcome;
		BinaryPrimitives.WriteInt32LittleEndian(
			frame.AsSpan(FrameLengthOffset, sizeof(int)),
			FrameSize);
		requestId.TryWriteBytes(frame.AsSpan(RequestIdOffset, 16));
		return frame;
	}

	private static bool HasEmptyIdentity(ReadOnlySpan<byte> frame)
	{
		return frame.Slice(ProcessIdOffset, ReservedOffset - ProcessIdOffset)
			.IndexOfAnyExcept((byte)0) < 0;
	}

	private static bool HasValidEnvelope(
		ReadOnlySpan<byte> frame,
		byte expectedFrameKind)
	{
		return (frame.Length == FrameSize) &&
			frame.Slice(MagicOffset, Magic.Length).SequenceEqual(Magic) &&
			(frame[FrameKindOffset] == expectedFrameKind) &&
			(frame[FlagsOffset] == 0) &&
			(BinaryPrimitives.ReadInt32LittleEndian(
				frame.Slice(FrameLengthOffset, sizeof(int))) == FrameSize) &&
			(frame.Slice(ReservedOffset).IndexOfAnyExcept((byte)0) < 0);
	}

	private static bool RequiresIdentity(UpdateShutdownOutcome outcome)
	{
		return outcome is UpdateShutdownOutcome.Observed or
			UpdateShutdownOutcome.Accepted or
			UpdateShutdownOutcome.Busy or
			UpdateShutdownOutcome.IdentityMismatch or
			UpdateShutdownOutcome.AlreadyShuttingDown;
	}

	private static Guid TryReadRequestId(ReadOnlySpan<byte> frame)
	{
		return frame.Length >= (RequestIdOffset + 16)
			? new Guid(frame.Slice(RequestIdOffset, 16))
			: Guid.Empty;
	}

	private static bool TryReadIdentity(
		ReadOnlySpan<byte> frame,
		out UpdateProcessIdentity identity)
	{
		identity = default;
		ushort generationIdLength = BinaryPrimitives.ReadUInt16LittleEndian(
			frame.Slice(GenerationIdLengthOffset, sizeof(ushort)));

		if ((generationIdLength == 0) ||
			(generationIdLength >
				UpdateProcessIdentity.MaximumPayloadGenerationIdLength) ||
			(frame.Slice(
				GenerationIdOffset + generationIdLength,
				UpdateProcessIdentity.MaximumPayloadGenerationIdLength -
					generationIdLength).IndexOfAnyExcept((byte)0) >= 0))
		{
			return false;
		}

		string generationId = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true).GetString(
				frame.Slice(GenerationIdOffset, generationIdLength));
		identity = new UpdateProcessIdentity(
			BinaryPrimitives.ReadInt32LittleEndian(
				frame.Slice(ProcessIdOffset, sizeof(int))),
			BinaryPrimitives.ReadInt64LittleEndian(
				frame.Slice(ProcessStartTimeOffset, sizeof(long))),
			generationId);
		return true;
	}

	private static void WriteIdentity(
		Span<byte> frame,
		UpdateProcessIdentity identity)
	{
		BinaryPrimitives.WriteInt32LittleEndian(
			frame.Slice(ProcessIdOffset, sizeof(int)),
			identity.ProcessId);
		BinaryPrimitives.WriteInt64LittleEndian(
			frame.Slice(ProcessStartTimeOffset, sizeof(long)),
			identity.ProcessStartTimeUtcTicks);
		int bytesWritten = Encoding.UTF8.GetBytes(
			identity.PayloadGenerationId,
			frame.Slice(
				GenerationIdOffset,
				UpdateProcessIdentity.MaximumPayloadGenerationIdLength));
		BinaryPrimitives.WriteUInt16LittleEndian(
			frame.Slice(GenerationIdLengthOffset, sizeof(ushort)),
			checked((ushort)bytesWritten));
	}
}
