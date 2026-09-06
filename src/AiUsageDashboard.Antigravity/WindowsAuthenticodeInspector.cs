using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed record WindowsAuthenticodeInspection(
	int WinVerifyTrustStatus,
	string? SignerSubject,
	string? SignerThumbprint);

internal interface IWindowsAuthenticodeInspector
{
	WindowsAuthenticodeInspection Inspect(string absolutePath);
}

internal sealed class WindowsAuthenticodeInspector : IWindowsAuthenticodeInspector
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WinTrustFileInfo
	{
		internal uint StructureSize;

		[MarshalAs(UnmanagedType.LPWStr)]
		internal string FilePath;

		internal IntPtr FileHandle;

		internal IntPtr KnownSubject;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WinTrustData
	{
		internal uint StructureSize;

		internal IntPtr PolicyCallbackData;

		internal IntPtr SipClientData;

		internal uint UiChoice;

		internal uint RevocationChecks;

		internal uint UnionChoice;

		internal IntPtr FileInfo;

		internal uint StateAction;

		internal IntPtr StateData;

		[MarshalAs(UnmanagedType.LPWStr)]
		internal string? UrlReference;

		internal uint ProviderFlags;

		internal uint UiContext;
	}

	private const uint CacheOnlyUrlRetrieval = 0x00001000;
	private const uint ChoiceFile = 1;
	private const uint RevocationCheckChainExcludeRoot = 0x00000080;
	private const uint RevokeWholeChain = 1;
	private const uint StateActionIgnore = 0;
	private const uint UiContextExecute = 0;
	private const uint UiNone = 2;
	private static readonly Guid GenericVerifyV2Action =
		new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

	public WindowsAuthenticodeInspection Inspect(string absolutePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Authenticode verification requires Windows.");
		}

		WinTrustFileInfo fileInfo = new()
		{
			StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
			FilePath = absolutePath,
			FileHandle = IntPtr.Zero,
			KnownSubject = IntPtr.Zero
		};
		IntPtr fileInfoPointer = Marshal.AllocHGlobal(
			Marshal.SizeOf<WinTrustFileInfo>());

		try
		{
			Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
			WinTrustData trustData = new()
			{
				StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
				PolicyCallbackData = IntPtr.Zero,
				SipClientData = IntPtr.Zero,
				UiChoice = UiNone,
				RevocationChecks = RevokeWholeChain,
				UnionChoice = ChoiceFile,
				FileInfo = fileInfoPointer,
				StateAction = StateActionIgnore,
				StateData = IntPtr.Zero,
				UrlReference = null,
				ProviderFlags =
					RevocationCheckChainExcludeRoot | CacheOnlyUrlRetrieval,
				UiContext = UiContextExecute
			};
			int trustStatus = WinVerifyTrust(
				IntPtr.Zero,
				GenericVerifyV2Action,
				ref trustData);
			(string? subject, string? thumbprint) = ReadSigner(absolutePath);
			return new WindowsAuthenticodeInspection(
				trustStatus,
				subject,
				thumbprint);
		}
		finally
		{
			Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
			Marshal.FreeHGlobal(fileInfoPointer);
		}
	}

	private static (string? Subject, string? Thumbprint) ReadSigner(
		string absolutePath)
	{
		try
		{
			using X509Certificate certificate =
				X509Certificate.CreateFromSignedFile(absolutePath);
			using X509Certificate2 certificateWithDetails = new(certificate);
			string? thumbprint = certificateWithDetails.Thumbprint?
				.Replace(" ", string.Empty, StringComparison.Ordinal)
				.ToUpperInvariant();
			return (certificateWithDetails.Subject, thumbprint);
		}
		catch (CryptographicException)
		{
			return (null, null);
		}
	}

	[DllImport(
		"wintrust.dll",
		CharSet = CharSet.Unicode,
		ExactSpelling = true,
		PreserveSig = true)]
	private static extern int WinVerifyTrust(
		IntPtr windowHandle,
		[MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
		ref WinTrustData trustData);
}
