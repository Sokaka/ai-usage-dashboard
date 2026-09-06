using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityPrivateKeyAclTests
{
	[Fact]
	public void IsAllowedOwner_AllowsOnlyFixedPrivilegedIdentities()
	{
		using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
		SecurityIdentifier currentProcessSid = currentIdentity.User ??
			throw new InvalidOperationException(
				"The current Windows identity has no user SID.");
		SecurityIdentifier localSystemSid = new(
			WellKnownSidType.LocalSystemSid,
			null);
		SecurityIdentifier administratorsSid = new(
			WellKnownSidType.BuiltinAdministratorsSid,
			null);
		SecurityIdentifier randomSid = new(
			"S-1-5-21-111111111-222222222-333333333-4444");

		Assert.True(
			AntigravityPrivateKeyAcl.IsAllowedOwner(currentProcessSid));
		Assert.True(AntigravityPrivateKeyAcl.IsAllowedOwner(localSystemSid));
		Assert.True(
			AntigravityPrivateKeyAcl.IsAllowedOwner(administratorsSid));
		Assert.False(AntigravityPrivateKeyAcl.IsAllowedOwner(randomSid));
	}

	[Fact]
	public void IsCurrentProcessOwner_RequiresExactCurrentUserSid()
	{
		using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
		SecurityIdentifier currentProcessSid = currentIdentity.User ??
			throw new InvalidOperationException(
				"The current Windows identity has no user SID.");
		SecurityIdentifier randomSid = new(
			"S-1-5-21-111111111-222222222-333333333-4444");

		Assert.True(
			AntigravityPrivateKeyAcl.IsCurrentProcessOwner(
				currentProcessSid));
		Assert.False(
			AntigravityPrivateKeyAcl.IsCurrentProcessOwner(randomSid));
	}

	[Fact]
	public void TryPrepareDirectory_WithNewDirectory_CreatesPrivateCurrentOwnedDirectory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string directoryPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"private-key"));

		bool isPrepared = AntigravityPrivateKeyAcl.TryPrepareDirectory(
			directoryPath,
			out bool wasCreated,
			out AntigravityPrivateKeyDirectoryFailureReason failureReason);

		Assert.True(isPrepared);
		Assert.True(wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.None,
			failureReason);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			directoryPath));
		DirectorySecurity security = new DirectoryInfo(directoryPath)
			.GetAccessControl(AccessControlSections.Owner);
		SecurityIdentifier owner = security.GetOwner(
			typeof(SecurityIdentifier)) as SecurityIdentifier ??
			throw new InvalidOperationException(
				"The private key directory owner is not a Windows SID.");
		Assert.True(AntigravityPrivateKeyAcl.IsCurrentProcessOwner(owner));
	}

	[Fact]
	public void TryPreparePrivateStorageDirectory_NewDirectoryIsPrivateBeforeAclCallback()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string providerPath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity");
		string privatePath = Path.Combine(providerPath, "private");
		bool wasPrivateInsideCallback = false;

		bool isPrepared =
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privatePath,
				out bool wasCreated,
				out AntigravityPrivateKeyDirectoryFailureReason failureReason,
				path =>
				{
					wasPrivateInsideCallback =
						AntigravityPrivateKeyAcl.IsPrivateDirectory(path);
					return wasPrivateInsideCallback;
				});

		Assert.True(isPrepared);
		Assert.True(wasCreated);
		Assert.True(wasPrivateInsideCallback);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.None,
			failureReason);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(providerPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(privatePath));
	}

	[Fact]
	public void TryPreparePrivateStorageDirectory_WithMissingProviderParent_CreatesPrivateHierarchy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string providerPath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity");
		string privatePath = Path.Combine(providerPath, "private");

		bool isPrepared =
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privatePath,
				out bool wasCreated,
				out AntigravityPrivateKeyDirectoryFailureReason failureReason);

		Assert.True(isPrepared);
		Assert.True(wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.None,
			failureReason);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			providerPath));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			privatePath));
	}

	[Fact]
	public void TryPreparePrivateStorageDirectory_WithExistingProviderCache_CreatesPrivateLeafWithoutChangingCache()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string providerPath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity");
		string cachePath = Path.Combine(
			providerPath,
			"account-001",
			"usage.json");
		string privatePath = Path.Combine(providerPath, "private");
		Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
		File.WriteAllText(cachePath, "synthetic-cache");
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			providerPath));

		bool isPrepared =
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privatePath,
				out bool wasCreated,
				out AntigravityPrivateKeyDirectoryFailureReason failureReason);

		Assert.True(isPrepared);
		Assert.True(wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.None,
			failureReason);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			privatePath));
		Assert.Equal("synthetic-cache", File.ReadAllText(cachePath));
	}

	[Fact]
	public void TryPreparePrivateStorageDirectory_WithProviderFile_RejectsWithoutReplacingIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string providerPath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity");
		string privatePath = Path.Combine(providerPath, "private");
		File.WriteAllText(providerPath, "synthetic-provider-file");

		bool isPrepared =
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privatePath,
				out bool wasCreated,
				out AntigravityPrivateKeyDirectoryFailureReason failureReason);

		Assert.False(isPrepared);
		Assert.False(wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.GrandparentInvalid,
			failureReason);
		Assert.Equal(
			"synthetic-provider-file",
			File.ReadAllText(providerPath));
		Assert.False(Directory.Exists(privatePath));
	}

	[Fact]
	public async Task TryPreparePrivateStorageDirectory_WithProviderJunction_RejectsWithoutFollowingIt()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string targetPath = Path.Combine(
			temporaryDirectory.Path,
			"junction-target");
		string providerPath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity");
		string privatePath = Path.Combine(providerPath, "private");
		Directory.CreateDirectory(targetPath);
		ProcessStartInfo startInfo = new(
			Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
		{
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add("mklink");
		startInfo.ArgumentList.Add("/J");
		startInfo.ArgumentList.Add(providerPath);
		startInfo.ArgumentList.Add(targetPath);
		using Process process = Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"The junction creation process did not start.");
		await process.WaitForExitAsync();
		Assert.Equal(0, process.ExitCode);

		try
		{
			bool isPrepared =
				AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
					privatePath,
					out bool wasCreated,
					out AntigravityPrivateKeyDirectoryFailureReason
						failureReason);

			Assert.False(isPrepared);
			Assert.False(wasCreated);
			Assert.Equal(
				AntigravityPrivateKeyDirectoryFailureReason.GrandparentInvalid,
				failureReason);
			Assert.Empty(Directory.EnumerateFileSystemEntries(targetPath));
		}
		finally
		{
			Directory.Delete(providerPath, false);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TryPreparePrivateStorageDirectory_WhenCreatedDirectoryBecomesNonEmpty_DoesNotDeleteIt(
		bool providerExistsInitially)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string providerPath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity");
		string privatePath = Path.Combine(providerPath, "private");

		if (providerExistsInitially)
		{
			Directory.CreateDirectory(providerPath);
		}

		string? aclTargetPath = null;
		bool isPrepared =
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privatePath,
				out bool wasCreated,
				out AntigravityPrivateKeyDirectoryFailureReason failureReason,
				path =>
				{
					aclTargetPath = path;
					File.WriteAllText(
						Path.Combine(path, "sentinel.txt"),
						"do-not-delete");
					return false;
				});
		string expectedTargetPath = providerExistsInitially
			? privatePath
			: providerPath;

		Assert.False(isPrepared);
		Assert.Equal(providerExistsInitially, wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.ApplyAclFailed,
			failureReason);
		Assert.Equal(expectedTargetPath, aclTargetPath);
		Assert.True(Directory.Exists(expectedTargetPath));
		Assert.Equal(
			"do-not-delete",
			File.ReadAllText(Path.Combine(
				expectedTargetPath,
				"sentinel.txt")));
		Assert.True(Directory.Exists(providerPath));
	}

	[Fact]
	public void TryPreparePrivateStorageDirectory_WhenNewLeafAclFails_CleansLeafAndAllowsRetry()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string providerPath = Path.Combine(
			temporaryDirectory.Path,
			"antigravity");
		string privatePath = Path.Combine(providerPath, "private");
		Directory.CreateDirectory(providerPath);

		bool firstPrepared =
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privatePath,
				out bool firstWasCreated,
				out AntigravityPrivateKeyDirectoryFailureReason
					firstFailureReason,
				_ => false);

		Assert.False(firstPrepared);
		Assert.True(firstWasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.ApplyAclFailed,
			firstFailureReason);
		Assert.False(Directory.Exists(privatePath));

		bool secondPrepared =
			AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				privatePath,
				out bool secondWasCreated,
				out AntigravityPrivateKeyDirectoryFailureReason
					secondFailureReason);

		Assert.True(secondPrepared);
		Assert.True(secondWasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.None,
			secondFailureReason);
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateDirectory(
			privatePath));
	}

	[Fact]
	public void TryPrepareDirectory_WithRelativePath_ReturnsInvalidPath()
	{
		bool isPrepared = AntigravityPrivateKeyAcl.TryPrepareDirectory(
			"relative-private-key-directory",
			out bool wasCreated,
			out AntigravityPrivateKeyDirectoryFailureReason failureReason);

		Assert.False(isPrepared);
		Assert.False(wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.InvalidPath,
			failureReason);
	}

	[Fact]
	public void TryPrepareDirectory_WithExistingFile_ReturnsExistingFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"not-a-directory"));
		File.WriteAllText(filePath, "synthetic");

		bool isPrepared = AntigravityPrivateKeyAcl.TryPrepareDirectory(
			filePath,
			out bool wasCreated,
			out AntigravityPrivateKeyDirectoryFailureReason failureReason);

		Assert.False(isPrepared);
		Assert.False(wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.ExistingFile,
			failureReason);
	}

	[Fact]
	public void TryPrepareDirectory_WithExistingNonPrivateDirectory_RefusesWithoutChangingAcl()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string directoryPath = Path.GetFullPath(Path.Combine(
			temporaryDirectory.Path,
			"existing-directory"));
		Directory.CreateDirectory(directoryPath);
		DirectoryInfo directory = new(directoryPath);
		string securityBefore = directory.GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner)
			.GetSecurityDescriptorSddlForm(AccessControlSections.All);

		Assert.False(
			AntigravityPrivateKeyAcl.IsPrivateDirectory(directoryPath));

		bool isPrepared = AntigravityPrivateKeyAcl.TryPrepareDirectory(
			directoryPath,
			out bool wasCreated,
			out AntigravityPrivateKeyDirectoryFailureReason failureReason);

		Assert.False(isPrepared);
		Assert.False(wasCreated);
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason
				.ExistingDirectoryNotPrivate,
			failureReason);
		string securityAfter = directory.GetAccessControl(
				AccessControlSections.Access | AccessControlSections.Owner)
			.GetSecurityDescriptorSddlForm(AccessControlSections.All);
		Assert.Equal(securityBefore, securityAfter);
	}
}
