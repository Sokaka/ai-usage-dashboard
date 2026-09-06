using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AiUsageDashboard.App.Providers;

internal sealed class CopilotStoredCredential
{
	internal string AccessToken { get; }

	internal string ProviderAccountIdentity { get; }

	internal CopilotStoredCredential(
		string providerAccountIdentity,
		string accessToken)
	{
		ProviderAccountIdentity = providerAccountIdentity;
		AccessToken = accessToken;
	}

	public override string ToString()
	{
		return nameof(CopilotStoredCredential);
	}
}

internal interface ICopilotCredentialStore
{
	CopilotStoredCredential? Read(Guid accountId);

	void Stage(Guid accountId, CopilotStoredCredential credential);

	bool CommitStaged(
		Guid accountId,
		string expectedProviderAccountIdentity);

	void RecoverStaged(
		Guid accountId,
		string? expectedProviderAccountIdentity);

	void DiscardStaged(Guid accountId);

	void Delete(Guid accountId);
}

internal interface IWindowsCredentialApi
{
	(string UserName, string Secret)? Read(string targetName);

	void Write(string targetName, string userName, string secret);

	void Delete(string targetName);
}

internal sealed class WindowsCopilotCredentialStore : ICopilotCredentialStore
{
	private const int MaximumAccessTokenLength = 4096;
	private const string TargetPrefix = "AiUsageDashboard/Copilot/";
	private const string PendingTargetPrefix =
		"AiUsageDashboard/Copilot/Pending/";
	private readonly IWindowsCredentialApi _credentialApi;

	internal WindowsCopilotCredentialStore()
		: this(new WindowsCredentialApi())
	{
	}

	internal WindowsCopilotCredentialStore(IWindowsCredentialApi credentialApi)
	{
		_credentialApi = credentialApi ??
			throw new ArgumentNullException(nameof(credentialApi));
	}

	public CopilotStoredCredential? Read(Guid accountId)
	{
		string targetName = CreateTargetName(accountId);
		(string UserName, string Secret)? value =
			_credentialApi.Read(targetName);
		if (value is null)
		{
			return null;
		}

		if (!CopilotAccountIdentityRules.TryParse(
				value.Value.UserName,
				out string? providerAccountIdentity) ||
			!TryValidateAccessToken(value.Value.Secret))
		{
			throw new CopilotClientException(
				CopilotFailureKind.AuthenticationRequired,
				"Copilot credential 格式無效，請重新連接帳號。");
		}

		return new CopilotStoredCredential(
			providerAccountIdentity!,
			value.Value.Secret);
	}

	public void Stage(Guid accountId, CopilotStoredCredential credential)
	{
		ArgumentNullException.ThrowIfNull(credential);
		string targetName = CreatePendingTargetName(accountId);
		if (!CopilotAccountIdentityRules.TryParse(
				credential.ProviderAccountIdentity,
				out string? providerAccountIdentity))
		{
			throw new ArgumentException(
				"Copilot provider account identity 格式無效。",
				nameof(credential));
		}

		if (!TryValidateAccessToken(credential.AccessToken))
		{
			throw new ArgumentException(
				"Copilot access token 格式無效。",
				nameof(credential));
		}

		_credentialApi.Write(
			targetName,
			providerAccountIdentity!,
			credential.AccessToken);
	}

	public bool CommitStaged(
		Guid accountId,
		string expectedProviderAccountIdentity)
	{
		if (!CopilotAccountIdentityRules.TryParse(
				expectedProviderAccountIdentity,
				out string? normalizedExpectedIdentity))
		{
			throw new ArgumentException(
				"Copilot expected provider account identity 格式無效。",
				nameof(expectedProviderAccountIdentity));
		}

		(string UserName, string Secret)? pending = _credentialApi.Read(
			CreatePendingTargetName(accountId));
		if (pending is null)
		{
			return false;
		}

		if (!CopilotAccountIdentityRules.AreEquivalent(
				pending.Value.UserName,
				normalizedExpectedIdentity) ||
			!TryValidateAccessToken(pending.Value.Secret))
		{
			throw new CopilotClientException(
				CopilotFailureKind.AccountMismatch,
				"Copilot pending credential 與卡片身分不同。");
		}

		_credentialApi.Write(
			CreateTargetName(accountId),
			normalizedExpectedIdentity!,
			pending.Value.Secret);
		try
		{
			_credentialApi.Delete(CreatePendingTargetName(accountId));
		}
		catch
		{
			(string UserName, string Secret)? committed =
				_credentialApi.Read(CreateTargetName(accountId));
			if ((committed is null) ||
				!CopilotAccountIdentityRules.AreEquivalent(
					committed.Value.UserName,
					normalizedExpectedIdentity) ||
				!string.Equals(
					committed.Value.Secret,
					pending.Value.Secret,
					StringComparison.Ordinal))
			{
				throw;
			}
		}

		return true;
	}

	public void RecoverStaged(
		Guid accountId,
		string? expectedProviderAccountIdentity)
	{
		(string UserName, string Secret)? pending = _credentialApi.Read(
			CreatePendingTargetName(accountId));
		if (pending is null)
		{
			return;
		}

		if (!CopilotAccountIdentityRules.TryParse(
				expectedProviderAccountIdentity,
				out string? normalizedExpectedIdentity) ||
			!CopilotAccountIdentityRules.AreEquivalent(
				pending.Value.UserName,
				normalizedExpectedIdentity) ||
			!TryValidateAccessToken(pending.Value.Secret))
		{
			DiscardStaged(accountId);
			return;
		}

		_credentialApi.Write(
			CreateTargetName(accountId),
			normalizedExpectedIdentity!,
			pending.Value.Secret);
		_credentialApi.Delete(CreatePendingTargetName(accountId));
	}

	public void DiscardStaged(Guid accountId)
	{
		_credentialApi.Delete(CreatePendingTargetName(accountId));
	}

	public void Delete(Guid accountId)
	{
		Exception? activeFailure = null;
		try
		{
			_credentialApi.Delete(CreateTargetName(accountId));
		}
		catch (Exception exception)
		{
			activeFailure = exception;
		}

		try
		{
			_credentialApi.Delete(CreatePendingTargetName(accountId));
		}
		catch (Exception pendingFailure)
		{
			if (activeFailure is not null)
			{
				throw new AggregateException(activeFailure, pendingFailure);
			}

			throw;
		}

		if (activeFailure is not null)
		{
			throw activeFailure;
		}
	}

	internal static string CreateTargetName(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Copilot 帳號識別碼不可為空。",
				nameof(accountId));
		}

		return $"{TargetPrefix}{accountId:N}";
	}

	internal static string CreatePendingTargetName(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Copilot 帳號識別碼不可為空。",
				nameof(accountId));
		}

		return $"{PendingTargetPrefix}{accountId:N}";
	}

	private static bool TryValidateAccessToken(string? value)
	{
		return !string.IsNullOrWhiteSpace(value) &&
			(value.Length <= MaximumAccessTokenLength) &&
			!value.Any(character =>
				char.IsControl(character) || char.IsWhiteSpace(character));
	}
}

internal sealed class WindowsCredentialApi : IWindowsCredentialApi
{
	private const int CredentialNotFound = 1168;
	private const int GenericCredentialType = 1;
	private const int LocalMachinePersistence = 2;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NativeCredential
	{
		internal uint Flags;
		internal uint Type;
		internal string TargetName;
		internal string? Comment;
		internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
		internal uint CredentialBlobSize;
		internal IntPtr CredentialBlob;
		internal uint Persist;
		internal uint AttributeCount;
		internal IntPtr Attributes;
		internal string? TargetAlias;
		internal string UserName;
	}

	public (string UserName, string Secret)? Read(string targetName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
		if (!CredRead(
				targetName,
				GenericCredentialType,
				0,
				out IntPtr credentialPointer))
		{
			int error = Marshal.GetLastWin32Error();
			if (error == CredentialNotFound)
			{
				return null;
			}

			throw new Win32Exception(
				error,
				"無法從 Windows Credential Manager 讀取 Copilot credential。");
		}

		try
		{
			NativeCredential credential =
				Marshal.PtrToStructure<NativeCredential>(credentialPointer);
			if ((credential.CredentialBlob == IntPtr.Zero) ||
				(credential.CredentialBlobSize == 0) ||
				((credential.CredentialBlobSize % sizeof(char)) != 0) ||
				string.IsNullOrWhiteSpace(credential.UserName))
			{
				return null;
			}

			string secret = Marshal.PtrToStringUni(
				credential.CredentialBlob,
				checked((int)credential.CredentialBlobSize / sizeof(char))) ??
				string.Empty;
			return (credential.UserName, secret);
		}
		finally
		{
			CredFree(credentialPointer);
		}
	}

	public void Write(string targetName, string userName, string secret)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
		ArgumentException.ThrowIfNullOrWhiteSpace(userName);
		ArgumentException.ThrowIfNullOrWhiteSpace(secret);
		IntPtr secretPointer = Marshal.StringToCoTaskMemUni(secret);
		try
		{
			NativeCredential credential = new()
			{
				Type = GenericCredentialType,
				TargetName = targetName,
				CredentialBlobSize = checked((uint)(secret.Length * sizeof(char))),
				CredentialBlob = secretPointer,
				Persist = LocalMachinePersistence,
				UserName = userName
			};

			if (!CredWrite(ref credential, 0))
			{
				throw new Win32Exception(
					Marshal.GetLastWin32Error(),
					"無法將 Copilot credential 寫入 Windows Credential Manager。");
			}
		}
		finally
		{
			Marshal.ZeroFreeCoTaskMemUnicode(secretPointer);
		}
	}

	public void Delete(string targetName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
		if (CredDelete(targetName, GenericCredentialType, 0))
		{
			return;
		}

		int error = Marshal.GetLastWin32Error();
		if (error != CredentialNotFound)
		{
			throw new Win32Exception(
				error,
				"無法從 Windows Credential Manager 刪除 Copilot credential。");
		}
	}

	[DllImport(
		"Advapi32.dll",
		EntryPoint = "CredDeleteW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CredDelete(
		string target,
		int type,
		int flags);

	[DllImport(
		"Advapi32.dll",
		EntryPoint = "CredFree",
		SetLastError = false)]
	private static extern void CredFree(IntPtr buffer);

	[DllImport(
		"Advapi32.dll",
		EntryPoint = "CredReadW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CredRead(
		string target,
		int type,
		int reservedFlag,
		out IntPtr credentialPointer);

	[DllImport(
		"Advapi32.dll",
		EntryPoint = "CredWriteW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CredWrite(
		ref NativeCredential credential,
		int flags);
}
