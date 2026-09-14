#pragma warning disable GHCP001

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Providers;

using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace AiUsageDashboard.Tests;

public sealed class CopilotSubscriptionRpcTests
{
	[Theory]
	[InlineData("token", true)]
	[InlineData("token", false)]
	[InlineData("env", true)]
	[InlineData("env", false)]
	public async Task ReadAsync_WithOldOrCredentialFreeAuth_UsesOneSdkRpc(
		string authType,
		bool includesToken)
	{
		Dictionary<string, object?> authInfo = new()
		{
			["host"] = "https://github.com",
			["copilotUser"] = new
			{
				login = "synthetic-user",
				copilot_plan = "individual",
				token_based_billing = true,
				quota_snapshots = new
				{
					premium_interactions = new { token_based_billing = true }
				}
			}
		};
		if (includesToken)
		{
			authInfo["token"] = "synthetic-not-a-credential";
		}
		if (authType == "env")
		{
			authInfo["envVar"] = "COPILOT_SDK_AUTH_TOKEN";
			authInfo["login"] = "synthetic-user";
		}
		authInfo["type"] = authType;
		var response = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = 1,
			result = new { authInfo }
		});

		var result = await ReadFixtureAsync(response);

		var actualAuth = result.GetProperty("authInfo");
		Assert.Equal(authType, actualAuth.GetProperty("type").GetString());
		Assert.Equal(includesToken, actualAuth.TryGetProperty("token", out _));
		var user = actualAuth.GetProperty("copilotUser");
		Assert.Equal("synthetic-user", user.GetProperty("login").GetString());
		Assert.True(user.GetProperty("token_based_billing").GetBoolean());
		Assert.True(user.GetProperty("quota_snapshots")
			.GetProperty("premium_interactions")
			.GetProperty("token_based_billing").GetBoolean());
	}

	[Fact]
	public async Task ReadAsync_WithRpcError_PreservesSdkFailureWithoutRetry()
	{
		const string response = """
			{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Synthetic method unavailable"}}
			""";

		var exception = await Assert.ThrowsAsync<IOException>(
			() => ReadFixtureAsync(response));

		Assert.NotNull(exception.InnerException);
		Assert.Equal("RemoteRpcException", exception.InnerException.GetType().Name);
		Assert.Contains("Synthetic method unavailable", exception.Message);
	}

	[Fact]
	public void SdkBinding_UsesPinnedPackageIdentity()
	{
		var assembly = typeof(CopilotClient).Assembly;

		Assert.Equal("GitHub.Copilot.SDK", assembly.GetName().Name);
		Assert.Equal(new Version(1, 0, 11, 0), assembly.GetName().Version);
		Assert.Equal("CC7B13FFCD2DDD51",
			Convert.ToHexString(assembly.GetName().GetPublicKeyToken()!));
		Assert.Equal("1.0.11+a550258d5c37bd662197536992a23d633bfe5804",
			assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
				.InformationalVersion);
	}

	private static async Task<JsonElement> ReadFixtureAsync(string response)
	{
		using MemoryStream requests = new();
		using MemoryStream responses = new(CreateFrame(response));
		var sdkAssembly = typeof(CopilotClient).Assembly;
		var rpcType = sdkAssembly.GetType("GitHub.Copilot.JsonRpc", throwOnError: true)!;
		var serializerProperty = typeof(CopilotClient).GetProperty(
			"SerializerOptionsForMessageFormatter",
			BindingFlags.Static | BindingFlags.NonPublic) ??
			throw new MissingMemberException("Pinned SDK serializer property is missing.");
		var serializer = Assert.IsType<JsonSerializerOptions>(
			serializerProperty.GetValue(null));
		using var rpc = Assert.IsAssignableFrom<IDisposable>(Activator.CreateInstance(
			rpcType, requests, responses, serializer, null));
		var accountConstructor = typeof(ServerAccountApi).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: [rpcType],
			modifiers: null) ??
			throw new MissingMethodException("Pinned SDK account constructor is missing.");
		var accountApi = Assert.IsType<ServerAccountApi>(accountConstructor.Invoke([rpc]));
		var startListening = rpcType.GetMethod("StartListening") ??
			throw new MissingMethodException("Pinned SDK read loop entry is missing.");
		var completionProperty = rpcType.GetProperty("Completion") ??
			throw new MissingMemberException("Pinned SDK completion property is missing.");
		var completion = Assert.IsAssignableFrom<Task>(completionProperty.GetValue(rpc));

		// 先登記唯一請求，再開啟已備妥回應的 stream，避免 EOF 搶先結束 SDK read loop。
		var pending = CopilotSubscriptionRpc.ReadAsync(accountApi, CancellationToken.None);
		startListening.Invoke(rpc, null);
		try
		{
			return await pending.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			await completion.WaitAsync(TimeSpan.FromSeconds(10));
			AssertSingleSubscriptionRequest(requests.ToArray());
		}
	}

	private static byte[] CreateFrame(string message)
	{
		var payload = Encoding.UTF8.GetBytes(message);
		var header = Encoding.ASCII.GetBytes(FormattableString.Invariant(
			$"Content-Length: {payload.Length}\r\n\r\n"));
		return [.. header, .. payload];
	}

	private static void AssertSingleSubscriptionRequest(byte[] frame)
	{
		var headerEnd = Encoding.ASCII.GetString(frame)
			.IndexOf("\r\n\r\n", StringComparison.Ordinal);
		Assert.True(headerEnd > 0, "The SDK must send one complete framed request.");
		var header = Encoding.ASCII.GetString(frame, 0, headerEnd);
		const string prefix = "Content-Length: ";
		Assert.StartsWith(prefix, header, StringComparison.Ordinal);
		var payloadLength = int.Parse(header[prefix.Length..], CultureInfo.InvariantCulture);
		var payloadStart = headerEnd + 4;
		Assert.Equal(payloadStart + payloadLength, frame.Length);
		using var request = JsonDocument.Parse(frame.AsMemory(payloadStart, payloadLength));
		Assert.Equal("2.0", request.RootElement.GetProperty("jsonrpc").GetString());
		Assert.Equal(1L, request.RootElement.GetProperty("id").GetInt64());
		Assert.Equal("account.getCurrentAuth", request.RootElement.GetProperty("method").GetString());
		Assert.False(request.RootElement.TryGetProperty("params", out _));
	}
}
