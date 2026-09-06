using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityPrivateKeyDirectoryFailureReason
{
	None,
	InvalidPath,
	ExistingFile,
	ExistingDirectoryNotPrivate,
	GrandparentInvalid,
	CreateDirectoryFailed,
	NewDirectoryInvalid,
	ApplyAclFailed,
	FinalValidationFailed,
	Unexpected
}

internal static class AntigravityPrivateKeyAcl
{
	[StructLayout(LayoutKind.Sequential)]
	private struct SecurityAttributes
	{
		internal int Length;
		internal IntPtr SecurityDescriptor;

		[MarshalAs(UnmanagedType.Bool)]
		internal bool InheritHandle;
	}

	private const int ErrorAlreadyExists = 183;

	internal static bool IsAllowedOwner(SecurityIdentifier owner)
	{
		ArgumentNullException.ThrowIfNull(owner);

		try
		{
			return GetAllowedIdentities().Contains(owner);
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsCurrentProcessOwner(SecurityIdentifier owner)
	{
		ArgumentNullException.ThrowIfNull(owner);

		try
		{
			return owner.Equals(GetCurrentProcessSid());
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsPrivateDirectory(string absoluteDirectoryPath)
	{
		try
		{
			if (!IsExistingNonReparseDirectoryPath(absoluteDirectoryPath))
			{
				return false;
			}

			DirectoryInfo directory = new(Path.GetFullPath(absoluteDirectoryPath));
			DirectorySecurity security = directory.GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner);
			SecurityIdentifier owner = GetOwner(security);
			HashSet<SecurityIdentifier> allowedIdentities =
				GetAllowedIdentities();
			return allowedIdentities.Contains(owner) &&
				HasPrivateRules(security, allowedIdentities, true);
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsPrivateFile(string absoluteFilePath)
	{
		try
		{
			if (!IsExistingRegularNonReparseFile(absoluteFilePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(absoluteFilePath);
			string? parentPath = Path.GetDirectoryName(fullPath);

			if ((parentPath is null) || !IsPrivateDirectory(parentPath))
			{
				return false;
			}

			DirectorySecurity parentSecurity = new DirectoryInfo(parentPath)
				.GetAccessControl(AccessControlSections.Owner);
			SecurityIdentifier directoryOwner = GetOwner(parentSecurity);
			FileInfo file = new(fullPath);
			FileSecurity fileSecurity = file.GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner);
			HashSet<SecurityIdentifier> allowedIdentities =
				GetAllowedIdentities();
			SecurityIdentifier fileOwner = GetOwner(fileSecurity);

			return allowedIdentities.Contains(directoryOwner) &&
				allowedIdentities.Contains(fileOwner) &&
				HasPrivateRules(fileSecurity, allowedIdentities, false);
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsPrivateOrSafelyInheritedFile(
		string absoluteFilePath)
	{
		if (IsPrivateFile(absoluteFilePath))
		{
			return true;
		}

		try
		{
			if (!IsExistingRegularNonReparseFile(absoluteFilePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(absoluteFilePath);
			string? parentPath = Path.GetDirectoryName(fullPath);

			if ((parentPath is null) || !IsPrivateDirectory(parentPath))
			{
				return false;
			}

			FileSecurity security = new FileInfo(fullPath).GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner);

			if (security.AreAccessRulesProtected ||
				(security.GetOwner(typeof(SecurityIdentifier)) is not
					SecurityIdentifier owner))
			{
				return false;
			}

			HashSet<SecurityIdentifier> allowedIdentities =
				GetAllowedIdentities();
			HashSet<SecurityIdentifier> observedIdentities = new();

			if (!allowedIdentities.Contains(owner))
			{
				return false;
			}

			foreach (FileSystemAccessRule rule in security.GetAccessRules(
				true,
				true,
				typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
			{
				if (!rule.IsInherited ||
					(rule.AccessControlType != AccessControlType.Allow) ||
					(rule.IdentityReference is not SecurityIdentifier identity) ||
					!allowedIdentities.Contains(identity) ||
					((rule.FileSystemRights & FileSystemRights.FullControl) !=
						FileSystemRights.FullControl) ||
					(rule.InheritanceFlags != InheritanceFlags.None) ||
					(rule.PropagationFlags != PropagationFlags.None) ||
					!observedIdentities.Add(identity))
				{
					return false;
				}
			}

			return allowedIdentities.SetEquals(observedIdentities);
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryPrepareDirectory(
		string absoluteDirectoryPath,
		out bool wasCreated)
	{
		return TryPrepareDirectory(
			absoluteDirectoryPath,
			out wasCreated,
			out _);
	}

	internal static bool TryPreparePrivateStorageDirectory(
		string absoluteDirectoryPath,
		out bool wasCreated,
		out AntigravityPrivateKeyDirectoryFailureReason failureReason)
	{
		return TryPreparePrivateStorageDirectory(
			absoluteDirectoryPath,
			out wasCreated,
			out failureReason,
			TryApplyPrivateDirectoryAcl);
	}

	internal static bool TryPreparePrivateStorageDirectory(
		string absoluteDirectoryPath,
		out bool wasCreated,
		out AntigravityPrivateKeyDirectoryFailureReason failureReason,
		Func<string, bool> tryApplyPrivateDirectoryAcl)
	{
		ArgumentNullException.ThrowIfNull(tryApplyPrivateDirectoryAcl);
		wasCreated = false;
		failureReason = AntigravityPrivateKeyDirectoryFailureReason.None;

		try
		{
			if (!OperatingSystem.IsWindows() ||
				!Path.IsPathFullyQualified(absoluteDirectoryPath))
			{
				failureReason =
					AntigravityPrivateKeyDirectoryFailureReason.InvalidPath;
				return false;
			}

			string fullPath = Path.GetFullPath(absoluteDirectoryPath);
			string? providerPath = Path.GetDirectoryName(fullPath);

			if (providerPath is null)
			{
				failureReason =
					AntigravityPrivateKeyDirectoryFailureReason.InvalidPath;
				return false;
			}

			if (Directory.Exists(providerPath))
			{
				if (!IsExistingNonReparseDirectoryPath(providerPath))
				{
					failureReason = AntigravityPrivateKeyDirectoryFailureReason
						.GrandparentInvalid;
					return false;
				}
			}
			else
			{
				if (File.Exists(providerPath))
				{
					failureReason = AntigravityPrivateKeyDirectoryFailureReason
						.GrandparentInvalid;
					return false;
				}

				string? applicationPath = Path.GetDirectoryName(providerPath);

				if ((applicationPath is null) ||
					!IsExistingNonReparseDirectoryPath(applicationPath))
				{
					failureReason = AntigravityPrivateKeyDirectoryFailureReason
						.GrandparentInvalid;
					return false;
				}

				bool isProviderPrepared = TryPrepareDirectory(
					providerPath,
					out bool wasProviderCreated,
					out AntigravityPrivateKeyDirectoryFailureReason
						providerFailureReason,
					tryApplyPrivateDirectoryAcl);

				if (!isProviderPrepared)
				{
					if (wasProviderCreated)
					{
						TryDeleteEmptyCreatedDirectory(providerPath);
					}

					failureReason = providerFailureReason;
					return false;
				}
			}

			bool isPrepared = TryPrepareDirectory(
				fullPath,
				out wasCreated,
				out failureReason,
				tryApplyPrivateDirectoryAcl);

			if (!isPrepared && wasCreated)
			{
				TryDeleteEmptyCreatedDirectory(fullPath);
			}

			return isPrepared;
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException))
		{
			failureReason =
				AntigravityPrivateKeyDirectoryFailureReason.InvalidPath;
			return false;
		}
		catch
		{
			failureReason =
				AntigravityPrivateKeyDirectoryFailureReason.Unexpected;
			return false;
		}
	}

	internal static bool TryPrepareDirectory(
		string absoluteDirectoryPath,
		out bool wasCreated,
		out AntigravityPrivateKeyDirectoryFailureReason failureReason)
	{
		return TryPrepareDirectory(
			absoluteDirectoryPath,
			out wasCreated,
			out failureReason,
			TryApplyPrivateDirectoryAcl);
	}

	private static bool TryPrepareDirectory(
		string absoluteDirectoryPath,
		out bool wasCreated,
		out AntigravityPrivateKeyDirectoryFailureReason failureReason,
		Func<string, bool> tryApplyPrivateDirectoryAcl)
	{
		ArgumentNullException.ThrowIfNull(tryApplyPrivateDirectoryAcl);
		wasCreated = false;
		failureReason = AntigravityPrivateKeyDirectoryFailureReason.None;

		try
		{
			if (!OperatingSystem.IsWindows() ||
				!Path.IsPathFullyQualified(absoluteDirectoryPath))
			{
				failureReason =
					AntigravityPrivateKeyDirectoryFailureReason.InvalidPath;
				return false;
			}

			string fullPath = Path.GetFullPath(absoluteDirectoryPath);

			if (Directory.Exists(fullPath))
			{
				if (IsPrivateDirectory(fullPath))
				{
					return true;
				}

				failureReason = AntigravityPrivateKeyDirectoryFailureReason
					.ExistingDirectoryNotPrivate;
				return false;
			}

			if (File.Exists(fullPath))
			{
				failureReason =
					AntigravityPrivateKeyDirectoryFailureReason.ExistingFile;
				return false;
			}

			string? grandparentPath = Path.GetDirectoryName(fullPath);

			if ((grandparentPath is null) ||
				!IsExistingNonReparseDirectoryPath(grandparentPath))
			{
				failureReason =
					AntigravityPrivateKeyDirectoryFailureReason.GrandparentInvalid;
				return false;
			}

			if (!TryCreatePrivateDirectory(
				fullPath,
				out int createDirectoryError))
			{
				if (createDirectoryError != ErrorAlreadyExists)
				{
					failureReason = AntigravityPrivateKeyDirectoryFailureReason
						.CreateDirectoryFailed;
					return false;
				}

				if (IsPrivateDirectory(fullPath))
				{
					return true;
				}

				failureReason = AntigravityPrivateKeyDirectoryFailureReason
					.ExistingDirectoryNotPrivate;
				return false;
			}

			wasCreated = true;

			if (!IsExistingNonReparseDirectoryPath(fullPath))
			{
				failureReason =
					AntigravityPrivateKeyDirectoryFailureReason.NewDirectoryInvalid;
				return false;
			}

			if (!tryApplyPrivateDirectoryAcl(fullPath))
			{
				failureReason =
					AntigravityPrivateKeyDirectoryFailureReason.ApplyAclFailed;
				return false;
			}

			if (!IsPrivateDirectory(fullPath))
			{
				failureReason = AntigravityPrivateKeyDirectoryFailureReason
					.FinalValidationFailed;
				return false;
			}

			return true;
		}
		catch (Exception exception) when (
			(exception is ArgumentException) ||
			(exception is NotSupportedException) ||
			(exception is PathTooLongException))
		{
			failureReason =
				AntigravityPrivateKeyDirectoryFailureReason.InvalidPath;
			return false;
		}
		catch
		{
			failureReason =
				AntigravityPrivateKeyDirectoryFailureReason.Unexpected;
			return false;
		}
	}

	internal static bool TryProtectNewFile(string absoluteFilePath)
	{
		try
		{
			if (!OperatingSystem.IsWindows() ||
				!IsExistingRegularNonReparseFile(absoluteFilePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(absoluteFilePath);
			string? parentPath = Path.GetDirectoryName(fullPath);

			if ((parentPath is null) || !IsPrivateDirectory(parentPath))
			{
				return false;
			}

			DirectorySecurity parentSecurity = new DirectoryInfo(parentPath)
				.GetAccessControl(AccessControlSections.Owner);
			SecurityIdentifier directoryOwner = GetOwner(parentSecurity);
			HashSet<SecurityIdentifier> allowedIdentities =
				GetAllowedIdentities();
			FileInfo file = new(fullPath);
			FileSecurity security = file.GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner);
			SecurityIdentifier fileOwner = GetOwner(security);

			if (!allowedIdentities.Contains(directoryOwner) ||
				!allowedIdentities.Contains(fileOwner))
			{
				return false;
			}

			security.SetAccessRuleProtection(true, false);

			foreach (FileSystemAccessRule rule in security.GetAccessRules(
				true,
				false,
				typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray())
			{
				security.RemoveAccessRuleSpecific(rule);
			}

			foreach (SecurityIdentifier identity in allowedIdentities)
			{
				security.AddAccessRule(new FileSystemAccessRule(
					identity,
					FileSystemRights.FullControl,
					AccessControlType.Allow));
			}

			file.SetAccessControl(security);
			return IsPrivateFile(fullPath);
		}
		catch
		{
			return false;
		}
	}

	internal static void TryDeleteEmptyCreatedDirectory(
		string absoluteDirectoryPath)
	{
		try
		{
			if (!IsExistingNonReparseDirectoryPath(absoluteDirectoryPath) ||
				Directory.EnumerateFileSystemEntries(absoluteDirectoryPath).Any())
			{
				return;
			}

			Directory.Delete(absoluteDirectoryPath, false);
		}
		catch
		{
			// Best-effort cleanup must not mask the fail-closed result.
		}
	}

	private static HashSet<SecurityIdentifier> GetAllowedIdentities()
	{
		return new HashSet<SecurityIdentifier>
		{
			GetCurrentProcessSid(),
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
			new SecurityIdentifier(
				WellKnownSidType.BuiltinAdministratorsSid,
				null)
		};
	}

	private static SecurityIdentifier GetCurrentProcessSid()
	{
		using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
		return currentIdentity.User ??
			throw new InvalidOperationException(
				"The current Windows identity has no user SID.");
	}

	private static SecurityIdentifier GetOwner(ObjectSecurity security)
	{
		return security.GetOwner(typeof(SecurityIdentifier)) as
			SecurityIdentifier ??
			throw new InvalidOperationException(
				"The private key ACL owner is not a Windows SID.");
	}

	private static bool HasPrivateRules(
		FileSystemSecurity security,
		HashSet<SecurityIdentifier> allowedIdentities,
		bool isDirectory)
	{
		if (!security.AreAccessRulesProtected)
		{
			return false;
		}

		HashSet<SecurityIdentifier> identitiesWithFullControl = new();
		InheritanceFlags expectedInheritance = isDirectory
			? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
			: InheritanceFlags.None;

		foreach (FileSystemAccessRule rule in security.GetAccessRules(
			true,
			true,
			typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
		{
			if (rule.IsInherited ||
				(rule.AccessControlType != AccessControlType.Allow) ||
				(rule.IdentityReference is not SecurityIdentifier identity) ||
				!allowedIdentities.Contains(identity) ||
				((rule.FileSystemRights & FileSystemRights.FullControl) !=
					FileSystemRights.FullControl) ||
				(rule.InheritanceFlags != expectedInheritance) ||
				(rule.PropagationFlags != PropagationFlags.None))
			{
				return false;
			}

			identitiesWithFullControl.Add(identity);
		}

		return allowedIdentities.SetEquals(identitiesWithFullControl);
	}

	private static bool IsExistingNonReparseDirectoryPath(
		string absoluteDirectoryPath)
	{
		return IsExistingNonReparsePath(absoluteDirectoryPath, true);
	}

	private static bool IsExistingRegularNonReparseFile(
		string absoluteFilePath)
	{
		return IsExistingNonReparsePath(absoluteFilePath, false);
	}

	private static bool IsExistingNonReparsePath(
		string absolutePath,
		bool expectDirectory)
	{
		try
		{
			if (!Path.IsPathFullyQualified(absolutePath))
			{
				return false;
			}

			string fullPath = Path.GetFullPath(absolutePath);
			string? root = Path.GetPathRoot(fullPath);

			if (string.IsNullOrWhiteSpace(root))
			{
				return false;
			}

			FileAttributes rootAttributes = File.GetAttributes(root);

			if (((rootAttributes & FileAttributes.Directory) == 0) ||
				((rootAttributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			string relativePath = Path.GetRelativePath(root, fullPath);

			if (string.Equals(relativePath, ".", StringComparison.Ordinal))
			{
				return expectDirectory;
			}

			string[] components = relativePath.Split(
				new[]
				{
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar
				},
				StringSplitOptions.RemoveEmptyEntries);

			if ((components.Length == 0) ||
				components.Any(component =>
					(component == ".") || (component == "..")))
			{
				return false;
			}

			string currentPath = root;

			for (int index = 0; index < components.Length; index++)
			{
				currentPath = Path.Combine(currentPath, components[index]);
				FileAttributes attributes = File.GetAttributes(currentPath);
				bool isDirectory =
					(attributes & FileAttributes.Directory) != 0;

				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					return false;
				}

				bool isFinalComponent = index == (components.Length - 1);

				if ((!isFinalComponent && !isDirectory) ||
					(isFinalComponent && (isDirectory != expectDirectory)))
				{
					return false;
				}
			}

			return expectDirectory
				? Directory.Exists(fullPath)
				: File.Exists(fullPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryApplyPrivateDirectoryAcl(
		string absoluteDirectoryPath)
	{
		try
		{
			DirectoryInfo directory = new(absoluteDirectoryPath);
			DirectorySecurity security = directory.GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner);
			SecurityIdentifier currentProcessSid = GetCurrentProcessSid();
			SecurityIdentifier owner = GetOwner(security);
			HashSet<SecurityIdentifier> allowedIdentities =
				GetAllowedIdentities();

			if (!allowedIdentities.Contains(owner))
			{
				return false;
			}

			if (!owner.Equals(currentProcessSid))
			{
				security.SetOwner(currentProcessSid);
			}

			security.SetAccessRuleProtection(true, false);

			foreach (FileSystemAccessRule rule in security.GetAccessRules(
				true,
				false,
				typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray())
			{
				security.RemoveAccessRuleSpecific(rule);
			}

			foreach (SecurityIdentifier identity in allowedIdentities)
			{
				security.AddAccessRule(new FileSystemAccessRule(
					identity,
					FileSystemRights.FullControl,
					InheritanceFlags.ContainerInherit |
						InheritanceFlags.ObjectInherit,
					PropagationFlags.None,
					AccessControlType.Allow));
			}

			directory.SetAccessControl(security);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryCreatePrivateDirectory(
		string absoluteDirectoryPath,
		out int errorCode)
	{
		DirectorySecurity security = new();
		SecurityIdentifier currentProcessSid = GetCurrentProcessSid();
		security.SetOwner(currentProcessSid);
		security.SetAccessRuleProtection(true, false);

		foreach (SecurityIdentifier identity in GetAllowedIdentities())
		{
			security.AddAccessRule(new FileSystemAccessRule(
				identity,
				FileSystemRights.FullControl,
				InheritanceFlags.ContainerInherit |
					InheritanceFlags.ObjectInherit,
				PropagationFlags.None,
				AccessControlType.Allow));
		}

		byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
		GCHandle descriptorHandle = GCHandle.Alloc(
			descriptor,
			GCHandleType.Pinned);

		try
		{
			SecurityAttributes attributes = new()
			{
				Length = Marshal.SizeOf<SecurityAttributes>(),
				SecurityDescriptor = descriptorHandle.AddrOfPinnedObject(),
				InheritHandle = false
			};
			// DACL 必須在目錄出現的同一個 system call 生效，避免繼承 ACL 的競爭窗口。
			bool wasCreated = CreateDirectory(
				absoluteDirectoryPath,
				ref attributes);
			errorCode = wasCreated ? 0 : Marshal.GetLastWin32Error();
			return wasCreated;
		}
		finally
		{
			descriptorHandle.Free();
		}
	}

	[DllImport(
		"kernel32.dll",
		EntryPoint = "CreateDirectoryW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateDirectory(
		string path,
		ref SecurityAttributes securityAttributes);
}
