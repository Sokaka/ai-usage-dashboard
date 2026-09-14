using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace AiUsageDashboard.Updater;

internal static class WindowsShellShortcut
{
	// 使用固定 Windows COM ABI，避免依賴 trimming 無法分析的 RCW 或 dynamic。
	private enum ShellLinkMethod
	{
		GetPath = 3,
		GetDescription = 6,
		SetDescription = 7,
		GetWorkingDirectory = 8,
		SetWorkingDirectory = 9,
		GetArguments = 10,
		SetArguments = 11,
		GetHotkey = 12,
		SetHotkey = 13,
		GetShowCommand = 14,
		SetShowCommand = 15,
		GetIconLocation = 16,
		SetIconLocation = 17,
		SetPath = 20
	}

	private const int PersistFileLoadSlot = 5;
	private const int PersistFileSaveSlot = 6;
	private const uint InProcessServer = 1;
	private const uint MultiThreadedApartment = 0;
	private const int ChangedApartmentMode = unchecked((int)0x80010106);
	private const uint RawPath = 4;
	private const uint ReadOnlySharedStorage = 0x00000040;
	private const int MaximumStringCharacters = 32768;
	private const int ShellLinkHeaderSize = 76;
	private const int ShellLinkFlagsOffset = 20;
	private const uint RunAsUserFlag = 0x00002000;
	private static readonly Guid ShellLinkClassId =
		new("00021401-0000-0000-C000-000000000046");
	private static readonly Guid ShellLinkInterfaceId =
		new("000214F9-0000-0000-C000-000000000046");
	private static readonly Guid PersistFileInterfaceId =
		new("0000010B-0000-0000-C000-000000000046");

	internal static void WriteNew(string shortcutPath, ShellShortcutDefinition definition)
	{
		using (FileStream reservation = new(
			shortcutPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
		}

		WithShellLink(shortcutPath, (shellLink, persistFile) =>
		{
			SetString(shellLink, ShellLinkMethod.SetPath, definition.TargetPath);
			SetString(shellLink, ShellLinkMethod.SetWorkingDirectory, definition.WorkingDirectory);
			SetString(shellLink, ShellLinkMethod.SetArguments, definition.Arguments);
			SetString(shellLink, ShellLinkMethod.SetDescription, definition.Description);
			Marshal.ThrowExceptionForHR(GetMethod<SetIconLocation>(
				shellLink,
				(int)ShellLinkMethod.SetIconLocation)(shellLink, definition.IconPath, definition.IconIndex));
			Marshal.ThrowExceptionForHR(GetMethod<SetInteger>(
				shellLink,
				(int)ShellLinkMethod.SetShowCommand)(shellLink, definition.ShowCommand));
			Marshal.ThrowExceptionForHR(GetMethod<SetHotkey>(
				shellLink,
				(int)ShellLinkMethod.SetHotkey)(shellLink, definition.Hotkey));
			Marshal.ThrowExceptionForHR(GetMethod<SaveFile>(
				persistFile,
				PersistFileSaveSlot)(persistFile, shortcutPath, true));
		});
	}

	internal static ShellShortcutDefinition Read(string shortcutPath)
	{
		using FileStream shortcutFile = new(
			shortcutPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		Span<byte> header = stackalloc byte[ShellLinkHeaderSize];
		shortcutFile.ReadExactly(header);

		if ((BinaryPrimitives.ReadUInt32LittleEndian(header) != ShellLinkHeaderSize) ||
			((BinaryPrimitives.ReadUInt32LittleEndian(header[ShellLinkFlagsOffset..]) & RunAsUserFlag) != 0))
		{
			throw new InvalidDataException($"捷徑格式或系統管理員執行設定與此安裝不符：{shortcutPath}");
		}

		ShellShortcutDefinition? definition = null;
		WithShellLink(shortcutPath, (shellLink, persistFile) =>
		{
			Marshal.ThrowExceptionForHR(GetMethod<LoadFile>(
				persistFile,
				PersistFileLoadSlot)(persistFile, shortcutPath, ReadOnlySharedStorage));
			StringBuilder targetPath = new(MaximumStringCharacters);
			Marshal.ThrowExceptionForHR(GetMethod<GetPath>(
				shellLink,
				(int)ShellLinkMethod.GetPath)(
					shellLink,
					targetPath,
					targetPath.Capacity,
					IntPtr.Zero,
					RawPath));
			StringBuilder iconPath = new(MaximumStringCharacters);
			Marshal.ThrowExceptionForHR(GetMethod<GetIconLocation>(
				shellLink,
				(int)ShellLinkMethod.GetIconLocation)(
					shellLink,
					iconPath,
					iconPath.Capacity,
					out int iconIndex));
			Marshal.ThrowExceptionForHR(GetMethod<GetInteger>(
				shellLink,
				(int)ShellLinkMethod.GetShowCommand)(shellLink, out int showCommand));
			Marshal.ThrowExceptionForHR(GetMethod<GetHotkey>(
				shellLink,
				(int)ShellLinkMethod.GetHotkey)(shellLink, out ushort hotkey));
			definition = new ShellShortcutDefinition(
				targetPath.ToString(),
				ReadString(shellLink, ShellLinkMethod.GetWorkingDirectory),
				ReadString(shellLink, ShellLinkMethod.GetArguments),
				ReadString(shellLink, ShellLinkMethod.GetDescription),
				iconPath.ToString(),
				iconIndex,
				showCommand,
				hotkey);
		});
		return definition ?? throw new IOException($"Windows 未讀回捷徑內容：{shortcutPath}");
	}

	private static void WithShellLink(string shortcutPath, Action<IntPtr, IntPtr> operation)
	{
		int initializationResult = CoInitializeEx(IntPtr.Zero, MultiThreadedApartment);
		IntPtr shellLink = IntPtr.Zero;
		IntPtr persistFile = IntPtr.Zero;

		try
		{
			if (initializationResult != ChangedApartmentMode)
			{
				Marshal.ThrowExceptionForHR(initializationResult);
			}

			Guid classId = ShellLinkClassId;
			Guid shellLinkId = ShellLinkInterfaceId;
			Marshal.ThrowExceptionForHR(CoCreateInstance(
				ref classId,
				IntPtr.Zero,
				InProcessServer,
				ref shellLinkId,
				out shellLink));
			Guid persistFileId = PersistFileInterfaceId;
			Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
				shellLink,
				ref persistFileId,
				out persistFile));
			operation(shellLink, persistFile);
		}
		catch (COMException exception)
		{
			throw new IOException($"Windows Shell 捷徑操作失敗：{shortcutPath}。{exception.Message}", exception);
		}
		finally
		{
			if (persistFile != IntPtr.Zero)
			{
				Marshal.Release(persistFile);
			}

			if (shellLink != IntPtr.Zero)
			{
				Marshal.Release(shellLink);
			}

			if (initializationResult >= 0)
			{
				CoUninitialize();
			}
		}
	}

	private static TDelegate GetMethod<TDelegate>(IntPtr interfacePointer, int slot)
		where TDelegate : Delegate
	{
		IntPtr vtable = Marshal.ReadIntPtr(interfacePointer);
		IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
		return Marshal.GetDelegateForFunctionPointer<TDelegate>(method);
	}

	private static void SetString(IntPtr shellLink, ShellLinkMethod method, string value)
	{
		Marshal.ThrowExceptionForHR(GetMethod<SetStringValue>(shellLink, (int)method)(shellLink, value));
	}

	private static string ReadString(IntPtr shellLink, ShellLinkMethod method)
	{
		StringBuilder buffer = new(MaximumStringCharacters);
		Marshal.ThrowExceptionForHR(GetMethod<GetStringValue>(shellLink, (int)method)(shellLink, buffer, buffer.Capacity));
		return buffer.ToString();
	}

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	private delegate int GetPath(IntPtr instance, [Out] StringBuilder path, int capacity, IntPtr findData, uint flags);

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	private delegate int GetStringValue(IntPtr instance, [Out] StringBuilder value, int capacity);

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	private delegate int SetStringValue(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	private delegate int GetIconLocation(IntPtr instance, [Out] StringBuilder path, int capacity, out int index);

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	private delegate int SetIconLocation(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string path, int index);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int GetInteger(IntPtr instance, out int value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int SetInteger(IntPtr instance, int value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int GetHotkey(IntPtr instance, out ushort value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int SetHotkey(IntPtr instance, ushort value);

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	private delegate int LoadFile(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	private delegate int SaveFile(IntPtr instance, [MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool remember);

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern void CoUninitialize();

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IntPtr instance);
}
