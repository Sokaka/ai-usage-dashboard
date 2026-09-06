using System.IO;

namespace AiUsageDashboard.App.Providers;

internal enum CodexAppServerFailureCategory
{
	Authentication,
	Compatibility,
	Transient,
	Unknown
}

internal sealed class CodexAppServerFailureException : InvalidOperationException
{
	public CodexAppServerFailureCategory Category { get; }

	public long ErrorCode { get; }

	public string ServerMessage { get; }

	public CodexAppServerFailureException(
		string method,
		long errorCode,
		string serverMessage,
		CodexAppServerFailureCategory category)
		: base($"Codex app-server {method} 回傳錯誤（code {errorCode}）：{serverMessage}")
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(method);
		ArgumentException.ThrowIfNullOrWhiteSpace(serverMessage);
		Category = category;
		ErrorCode = errorCode;
		ServerMessage = serverMessage;
	}
}

internal sealed class CodexCliNotFoundException : FileNotFoundException
{
	public CodexCliNotFoundException(string message)
		: base(message)
	{
	}
}

internal sealed class CodexCliUntrustedException : InvalidOperationException
{
	public CodexCliUntrustedException(
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed class CodexCliContainmentException : InvalidOperationException
{
	internal const string RestartRequiredMessage =
		"無法確認 Codex CLI 程序是否已完全結束；請重新啟動 AI Usage 後再試。";

	public CodexCliContainmentException(
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed class CodexCliProbeException : InvalidOperationException
{
	public CodexCliProbeException(
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed class CodexUsageNotConfiguredException : InvalidOperationException
{
	public CodexUsageNotConfiguredException(string message)
		: base(message)
	{
	}
}

internal sealed class CodexUsageAccountActionRequiredException : InvalidOperationException
{
	public CodexUsageAccountActionRequiredException(string message)
		: base(message)
	{
	}
}

internal sealed class CodexWorkspaceConfigurationException : InvalidOperationException
{
	public CodexWorkspaceConfigurationException(string message)
		: base(message)
	{
	}

	public CodexWorkspaceConfigurationException(
		string message,
		Exception innerException)
		: base(message, innerException)
	{
	}
}

internal sealed class CodexWorkspaceMismatchException : InvalidOperationException
{
	public CodexWorkspaceMismatchException()
		: base("Codex workspace 與帳號卡片的綁定不同。")
	{
	}
}

internal sealed class CodexWorkspaceBindingMissingException : InvalidOperationException
{
	public CodexWorkspaceBindingMissingException()
		: base("Codex workspace 尚未綁定。")
	{
	}
}

internal sealed class CodexWorkspaceBindingInvalidException : InvalidOperationException
{
	public CodexWorkspaceBindingInvalidException()
		: base("Codex workspace binding 格式無效。")
	{
	}
}

internal interface ICodexUsagePoller
{
	Task<CodexUsagePollResult> PollAsync(
		Guid accountId,
		CancellationToken cancellationToken = default);

	Task<CodexUsagePollResult> PollBoundAsync(
		Guid accountId,
		string expectedPublicBindingIdentity,
		CancellationToken cancellationToken = default);
}
