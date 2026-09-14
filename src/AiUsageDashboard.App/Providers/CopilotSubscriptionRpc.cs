#pragma warning disable GHCP001

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace AiUsageDashboard.App.Providers;

internal static class CopilotSubscriptionRpc
{
	private sealed record RpcBinding(FieldInfo RpcField, MethodInfo InvokeMethod);

	private static readonly Lazy<RpcBinding> Binding = new(CreateBinding);

	internal static async Task<JsonElement> ReadAsync(
		ServerAccountApi accountApi,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(accountApi);
		cancellationToken.ThrowIfCancellationRequested();
		RpcBinding binding = Binding.Value;
		object rpc = binding.RpcField.GetValue(accountApi) ??
			throw new InvalidOperationException("Copilot account.getCurrentAuth transport 尚未連線。");
		object? pending;
		try
		{
			pending = binding.InvokeMethod.Invoke(
				null,
				[rpc, "account.getCurrentAuth", Array.Empty<object?>(), null, cancellationToken, null]);
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
			throw;
		}

		if (pending is not Task<JsonElement> request)
		{
			throw new NotSupportedException("Copilot account.getCurrentAuth 的 SDK task 契約不相容。");
		}

		return await request.ConfigureAwait(false);
	}

	private static RpcBinding CreateBinding()
	{
		// SDK 沒有公開 raw RPC 入口；此固定版本橋接的理由與升級要求見 TECHNICAL_OVERVIEW.md。
		Assembly sdk = typeof(CopilotClient).Assembly;
		FieldInfo? rpcField = typeof(ServerAccountApi).GetField(
			"_rpc", BindingFlags.Instance | BindingFlags.NonPublic);
		if ((sdk.GetName().Version != new Version(1, 0, 11, 0)) ||
			(rpcField?.FieldType.FullName != "GitHub.Copilot.JsonRpc") ||
			(rpcField.FieldType.Assembly != sdk))
		{
			throw new NotSupportedException("Copilot account.getCurrentAuth 的 SDK transport 契約不相容。");
		}

		MethodInfo[] candidates = typeof(CopilotClient)
			.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
			.Where(method => (method.Name == "InvokeRpcAsync") &&
				method.IsGenericMethodDefinition &&
				(method.GetGenericArguments().Length == 1) &&
				HasExpectedParameters(method, rpcField.FieldType))
			.ToArray();
		if (candidates.Length != 1)
		{
			throw new NotSupportedException("Copilot account.getCurrentAuth 的 SDK 呼叫契約不相容。");
		}

		MethodInfo invokeMethod = candidates[0].MakeGenericMethod(typeof(JsonElement));
		if (invokeMethod.ReturnType != typeof(Task<JsonElement>))
		{
			throw new NotSupportedException("Copilot account.getCurrentAuth 的 SDK 回傳契約不相容。");
		}
		return new RpcBinding(rpcField, invokeMethod);
	}

	private static bool HasExpectedParameters(MethodInfo method, Type rpcType)
	{
		ParameterInfo[] parameters = method.GetParameters();
		return (parameters.Length == 6) &&
			(parameters[0].ParameterType == rpcType) &&
			(parameters[1].ParameterType == typeof(string)) &&
			(parameters[2].ParameterType == typeof(object[])) &&
			(parameters[3].ParameterType == typeof(StringBuilder)) &&
			(parameters[4].ParameterType == typeof(CancellationToken)) &&
			(parameters[5].ParameterType == typeof(Action<JsonElement>));
	}
}
