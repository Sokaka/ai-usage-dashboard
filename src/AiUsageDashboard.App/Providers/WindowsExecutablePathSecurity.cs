using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal static class WindowsExecutablePathSecurity
{
	private const string TrustedInstallerSid =
		"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

	internal static bool IsCanonicalNonReparseFile(string absolutePath)
	{
		try
		{
			string fullPath = Path.GetFullPath(absolutePath);
			FileInfo file = new(fullPath);

			if (!file.Exists ||
				!string.Equals(file.FullName, fullPath, StringComparison.OrdinalIgnoreCase) ||
				((file.Attributes & FileAttributes.Directory) != 0) ||
				((file.Attributes & FileAttributes.ReparsePoint) != 0))
			{
				return false;
			}

			DirectoryInfo? directory = file.Directory;

			while (directory is not null)
			{
				if (!directory.Exists ||
					((directory.Attributes & FileAttributes.ReparsePoint) != 0))
				{
					return false;
				}

				directory = directory.Parent;
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsCanonicalNonReparseDirectory(string absolutePath)
	{
		try
		{
			string fullPath = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(absolutePath));
			DirectoryInfo? directory = new(fullPath);

			while (directory is not null)
			{
				if (!directory.Exists ||
					((directory.Attributes & FileAttributes.ReparsePoint) != 0))
				{
					return false;
				}

				directory = directory.Parent;
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsFixedDrivePath(string absolutePath)
	{
		try
		{
			string? root = Path.GetPathRoot(Path.GetFullPath(absolutePath));
			return !string.IsNullOrWhiteSpace(root) &&
				(new DriveInfo(root).DriveType == DriveType.Fixed);
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsPathAclSafe(string absolutePath)
	{
		try
		{
			string fullPath = Path.GetFullPath(absolutePath);
			string userProfilePath = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(userProfilePath) ||
				!Path.IsPathFullyQualified(userProfilePath))
			{
				return false;
			}

			string userProfile = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(userProfilePath));
			string userProfilePrefix =
				userProfile + Path.DirectorySeparatorChar;

			if (!fullPath.StartsWith(
					userProfilePrefix,
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			FileInfo file = new(fullPath);

			if (!IsAccessControlSafe(file.GetAccessControl(
					AccessControlSections.Access |
						AccessControlSections.Owner)))
			{
				return false;
			}

			DirectoryInfo? directory = file.Directory;

			while (directory is not null)
			{
				if (!IsAccessControlSafe(directory.GetAccessControl(
						AccessControlSections.Access |
							AccessControlSections.Owner)))
				{
					return false;
				}

				if (string.Equals(
					directory.FullName,
					userProfile,
					StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				directory = directory.Parent;
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsPathAclSafeWithinTrustedRoot(
		string absolutePath,
		string trustedRootPath)
	{
		try
		{
			string fullPath = Path.GetFullPath(absolutePath);
			string trustedRoot = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(trustedRootPath));

			if (!IsFixedDrivePath(fullPath) ||
				!IsCanonicalNonReparseFile(fullPath) ||
				!IsCanonicalNonReparseDirectory(trustedRoot) ||
				!IsDescendantPath(fullPath, trustedRoot))
			{
				return false;
			}

			FileInfo file = new(fullPath);

			if (!IsAccessControlSafe(file.GetAccessControl(
					AccessControlSections.Access |
						AccessControlSections.Owner)))
			{
				return false;
			}

			DirectoryInfo? directory = file.Directory;

			while (directory is not null)
			{
				if (!IsAccessControlSafe(directory.GetAccessControl(
						AccessControlSections.Access |
							AccessControlSections.Owner)))
				{
					return false;
				}

				if (string.Equals(
					directory.FullName,
					trustedRoot,
					StringComparison.OrdinalIgnoreCase))
				{
					return AreTrustedRootAncestorsSafe(directory);
				}

				directory = directory.Parent;
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsDirectoryPathAclSafe(string absolutePath)
	{
		try
		{
			string fullPath = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(absolutePath));
			string userProfilePath = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);

			if (string.IsNullOrWhiteSpace(userProfilePath) ||
				!Path.IsPathFullyQualified(userProfilePath))
			{
				return false;
			}

			string userProfile = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(userProfilePath));
			string userProfilePrefix =
				userProfile + Path.DirectorySeparatorChar;

			if (!string.Equals(
					fullPath,
					userProfile,
					StringComparison.OrdinalIgnoreCase) &&
				!fullPath.StartsWith(
					userProfilePrefix,
					StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			DirectoryInfo? directory = new(fullPath);

			while (directory is not null)
			{
				if (!directory.Exists ||
					!IsAccessControlSafe(directory.GetAccessControl(
						AccessControlSections.Access |
							AccessControlSections.Owner)))
				{
					return false;
				}

				if (string.Equals(
					directory.FullName,
					userProfile,
					StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				directory = directory.Parent;
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsDirectoryPathAclSafeWithinTrustedRoot(
		string absolutePath,
		string trustedRootPath)
	{
		try
		{
			string fullPath = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(absolutePath));
			string trustedRoot = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(trustedRootPath));

			if (!IsFixedDrivePath(fullPath) ||
				!IsCanonicalNonReparseDirectory(fullPath) ||
				!IsCanonicalNonReparseDirectory(trustedRoot) ||
				(!string.Equals(
					fullPath,
					trustedRoot,
					StringComparison.OrdinalIgnoreCase) &&
					!IsDescendantPath(fullPath, trustedRoot)))
			{
				return false;
			}

			DirectoryInfo? directory = new(fullPath);

			while (directory is not null)
			{
				if (!IsAccessControlSafe(directory.GetAccessControl(
						AccessControlSections.Access |
							AccessControlSections.Owner)))
				{
					return false;
				}

				if (string.Equals(
					directory.FullName,
					trustedRoot,
					StringComparison.OrdinalIgnoreCase))
				{
					return AreTrustedRootAncestorsSafe(directory);
				}

				directory = directory.Parent;
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsAccessControlSafe(FileSystemSecurity security)
	{
		const FileSystemRights unsafeRights =
			FileSystemRights.WriteData |
			FileSystemRights.AppendData |
			FileSystemRights.WriteExtendedAttributes |
			FileSystemRights.WriteAttributes |
			FileSystemRights.Delete |
			FileSystemRights.DeleteSubdirectoriesAndFiles |
			FileSystemRights.ChangePermissions |
			FileSystemRights.TakeOwnership;

		if (security.GetOwner(typeof(SecurityIdentifier)) is not
			SecurityIdentifier owner ||
			!AntigravityPrivateKeyAcl.IsAllowedOwner(owner))
		{
			return false;
		}

		AuthorizationRuleCollection rules = security.GetAccessRules(
			includeExplicit: true,
			includeInherited: true,
			targetType: typeof(SecurityIdentifier));

		foreach (FileSystemAccessRule rule in rules.OfType<FileSystemAccessRule>())
		{
			if ((rule.AccessControlType != AccessControlType.Allow) ||
				((rule.FileSystemRights & unsafeRights) == 0))
			{
				continue;
			}

			if ((rule.IdentityReference is not SecurityIdentifier identity) ||
				!AntigravityPrivateKeyAcl.IsAllowedOwner(identity))
			{
				return false;
			}
		}

		return true;
	}

	private static bool AreTrustedRootAncestorsSafe(
		DirectoryInfo trustedRoot)
	{
		DirectoryInfo? directory = trustedRoot.Parent;

		while (directory is not null)
		{
			bool isVolumeRoot = directory.Parent is null;

			if (!directory.Exists ||
				!IsAncestorAccessControlSafe(
					directory.GetAccessControl(
						AccessControlSections.Access |
							AccessControlSections.Owner),
					isVolumeRoot))
			{
				return false;
			}

			directory = directory.Parent;
		}

		return true;
	}

	private static bool IsAncestorAccessControlSafe(
		FileSystemSecurity security,
		bool isVolumeRoot)
	{
		const FileSystemRights unsafeAncestorRights =
			FileSystemRights.WriteExtendedAttributes |
			FileSystemRights.WriteAttributes |
			FileSystemRights.Delete |
			FileSystemRights.DeleteSubdirectoriesAndFiles |
			FileSystemRights.ChangePermissions |
			FileSystemRights.TakeOwnership;
		const FileSystemRights unsafeVolumeRootRights =
			FileSystemRights.DeleteSubdirectoriesAndFiles |
			FileSystemRights.ChangePermissions |
			FileSystemRights.TakeOwnership;

		if (security.GetOwner(typeof(SecurityIdentifier)) is not
			SecurityIdentifier owner ||
			!IsTrustedInstallerIdentity(owner))
		{
			return false;
		}

		FileSystemRights unsafeRights = isVolumeRoot
			? unsafeVolumeRootRights
			: unsafeAncestorRights;
		AuthorizationRuleCollection rules = security.GetAccessRules(
			includeExplicit: true,
			includeInherited: true,
			targetType: typeof(SecurityIdentifier));

		foreach (FileSystemAccessRule rule in rules.OfType<FileSystemAccessRule>())
		{
			if ((rule.AccessControlType != AccessControlType.Allow) ||
				((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) ||
				((rule.FileSystemRights & unsafeRights) == 0))
			{
				continue;
			}

			if ((rule.IdentityReference is not SecurityIdentifier identity) ||
				!IsTrustedInstallerIdentity(identity))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsDescendantPath(string path, string parentPath)
	{
		string parentPrefix = Path.EndsInDirectorySeparator(parentPath)
			? parentPath
			: parentPath + Path.DirectorySeparatorChar;
		return path.StartsWith(
			parentPrefix,
			StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsTrustedInstallerIdentity(
		SecurityIdentifier identity)
	{
		return AntigravityPrivateKeyAcl.IsAllowedOwner(identity) ||
			string.Equals(
				identity.Value,
				TrustedInstallerSid,
				StringComparison.Ordinal);
	}
}
