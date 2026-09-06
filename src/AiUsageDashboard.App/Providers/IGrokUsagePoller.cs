using System.IO;

using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.App.Providers;

internal enum GrokAcpFailureCategory
{
	Authentication,
	Compatibility,
	Transient,
	Unknown
}

internal sealed class GrokAcpFailureException : InvalidOperationException
{
	public GrokAcpFailureCategory Category { get; }

	public long? ErrorCode { get; }

	public GrokAcpFailureException(
		string method,
		GrokAcpFailureCategory category,
		long? errorCode = null,
		Exception? innerException = null)
		: base(CreateMessage(method, category, errorCode), innerException)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(method);
		Category = category;
		ErrorCode = errorCode;
	}

	private static string CreateMessage(
		string method,
		GrokAcpFailureCategory category,
		long? errorCode)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(method);
		return errorCode is null
			? $"Grok ACP {method} failed ({category})."
			: $"Grok ACP {method} failed ({category}, code {errorCode.Value}).";
	}
}

internal sealed class GrokCliNotFoundException : FileNotFoundException
{
	public GrokCliNotFoundException(string message)
		: base(message)
	{
	}
}

internal sealed class GrokCliUntrustedException : InvalidOperationException
{
	public GrokCliUntrustedException(string message)
		: base(message)
	{
	}
}

internal sealed class GrokUsageNotConfiguredException : InvalidOperationException
{
	public GrokUsageNotConfiguredException(string message)
		: base(message)
	{
	}
}

internal sealed class GrokUsageSchemaException : IOException
{
	public GrokUsageSchemaException(string message)
		: base(message)
	{
	}
}

internal sealed class GrokUnsupportedBillingException : InvalidOperationException
{
	public GrokUnsupportedBillingException(string message)
		: base(message)
	{
	}
}

internal sealed class GrokPrincipalMismatchException : InvalidOperationException
{
	public GrokPrincipalMismatchException()
		: base("Grok ACP observed principal does not match the bound account.")
	{
	}
}

internal sealed class GrokPrincipalBindingInvalidException : InvalidOperationException
{
	public GrokPrincipalBindingInvalidException()
		: base("Grok account binding is missing or invalid.")
	{
	}
}

internal sealed class GrokProcessContainmentException : IOException
{
	public GrokProcessContainmentException(string message)
		: base(message)
	{
	}

	public GrokProcessContainmentException(
		string message,
		Exception innerException)
		: base(message, innerException)
	{
	}
}

internal sealed class GrokCliVersionProbeException : IOException
{
	public GrokCliVersionProbeException(
		string message,
		Exception innerException)
		: base(message, innerException)
	{
	}
}

internal interface IGrokUsagePoller
{
	Task<GrokUsagePollResult> PollAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task<GrokUsagePollResult> PollBoundAsync(
		Guid accountId,
		GrokAccountBinding binding,
		CancellationToken cancellationToken = default);
}
