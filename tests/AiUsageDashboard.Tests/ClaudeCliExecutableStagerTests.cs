using System.Security.AccessControl;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeCliExecutableStagerTests
{
	private sealed class PrivateProfileDirectory : IDisposable
	{
		private readonly string _parentPath;

		internal string Path { get; }
		internal string SourceDirectory => _parentPath;

		internal PrivateProfileDirectory()
		{
			string userProfile = Environment.GetFolderPath(
				Environment.SpecialFolder.UserProfile);
			_parentPath = System.IO.Path.Combine(
				userProfile,
				$"AiUsageDashboard.ClaudeCli.Tests.{Guid.NewGuid():N}");
			Path = System.IO.Path.Combine(_parentPath, "executables-v1");
			Directory.CreateDirectory(_parentPath);
		}

		public void Dispose()
		{
			string userProfilePrefix = System.IO.Path.TrimEndingDirectorySeparator(
				System.IO.Path.GetFullPath(Environment.GetFolderPath(
					Environment.SpecialFolder.UserProfile))) +
				System.IO.Path.DirectorySeparatorChar;
			string fullParentPath = System.IO.Path.GetFullPath(_parentPath);
			if (!fullParentPath.StartsWith(
				userProfilePrefix,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					"The Claude CLI test directory is outside the user profile.");
			}

			if (Directory.Exists(fullParentPath))
			{
				Directory.Delete(fullParentPath, recursive: true);
			}
		}
	}

	private sealed class FakeClaudeCliExecutableStager :
		IClaudeCliExecutableStager
	{
		private readonly Func<string, string> _stage;

		internal List<string> ObservedSourcePaths { get; } = new();

		internal FakeClaudeCliExecutableStager(Func<string, string> stage)
		{
			_stage = stage;
		}

		public WindowsOfficialCliExecutableLease Stage(
			string sourceExecutablePath,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ObservedSourcePaths.Add(sourceExecutablePath);
			string protectedPath = _stage(sourceExecutablePath);
			return WindowsOfficialCliExecutableLease.CreateProtected(
				protectedPath,
				new FileStream(
					protectedPath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		}
	}

	private const string ExactSignerSubject =
		"CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", L=San Francisco, S=California, C=US";

	[Fact]
	public void Stage_WithAnthropicSignedSource_ProtectsOriginalPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "claude.exe");
		using ClaudeCliExecutableStager stager = new(
			_ => CreateSignature(ExactSignerSubject),
			_ => true,
			_ => true,
			_ => true);

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);

		Assert.True(lease.IsProtected);
		Assert.Equal(Path.GetFullPath(sourcePath), lease.ExecutablePath);
		Assert.Equal([Path.GetFullPath(sourcePath)], Directory.EnumerateFiles(
			temporaryDirectory.Path));
	}

	[Fact]
	public void Stage_WithNonAnthropicSigner_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "claude.exe");
		using ClaudeCliExecutableStager stager = new(
			_ => CreateSignature("CN=X.AI LLC, O=X.AI LLC"),
			_ => true,
			_ => true,
			_ => true);

		Assert.Throws<ClaudeCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
	}

	[Fact]
	public void ResolveExecutablePath_WithWritableCapabilityAce_UsesProtectedCopy()
	{
		using PrivateProfileDirectory protectedDirectory = new();
		string sourcePath = System.IO.Path.Combine(
			protectedDirectory.SourceDirectory,
			"claude.exe");
		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x01, 0x02]);
		Assert.True(WindowsExecutablePathSecurity.IsPathAclSafe(sourcePath));
		AddWritableCapabilityAce(sourcePath);

		Assert.False(WindowsExecutablePathSecurity.IsPathAclSafe(sourcePath));
		string protectedRoot = protectedDirectory.Path;
		int signatureInspections = 0;
		using ClaudeCliExecutableStager stager = new(
			_ =>
			{
				signatureInspections++;
				return CreateSignature(ExactSignerSubject);
			},
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			WindowsExecutablePathSecurity.IsPathAclSafe,
			() => protectedRoot);
		WindowsOfficialCliExecutableValidator validator =
			ClaudeCliUsagePoller.CreateOfficialExecutableValidator(
				_ => true,
				_ => throw new InvalidOperationException(
					"Protected validation must not repeat signature inspection."));

		string? firstPath = ClaudeCliUsagePoller.ResolveExecutablePath(
			[sourcePath],
			validator,
			stager);
		string? cachedPath = ClaudeCliUsagePoller.ResolveExecutablePath(
			[sourcePath],
			validator,
			stager);

		Assert.NotNull(firstPath);
		Assert.Equal(firstPath, cachedPath);
		Assert.NotEqual(Path.GetFullPath(sourcePath), firstPath);
		Assert.True(WindowsExecutablePathSecurity.IsPathAclSafe(firstPath));
		Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(firstPath));
		Assert.Equal(2, signatureInspections);

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x03, 0x04]);
		string? updatedPath = ClaudeCliUsagePoller.ResolveExecutablePath(
			[sourcePath],
			validator,
			stager);

		Assert.NotNull(updatedPath);
		Assert.NotEqual(firstPath, updatedPath);
		Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(updatedPath));
		Assert.Equal(4, signatureInspections);
	}

	[Fact]
	public void Stage_WithWritableCapabilityAceAndInvalidSigner_LeavesNoCopy()
	{
		using PrivateProfileDirectory protectedDirectory = new();
		string sourcePath = System.IO.Path.Combine(
			protectedDirectory.SourceDirectory,
			"claude.exe");
		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x01, 0x02]);
		AddWritableCapabilityAce(sourcePath);
		using ClaudeCliExecutableStager stager = new(
			_ => CreateSignature("CN=X.AI LLC, O=X.AI LLC"),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			WindowsExecutablePathSecurity.IsPathAclSafe,
			() => protectedDirectory.Path);

		Assert.Throws<ClaudeCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
		Assert.False(Directory.Exists(protectedDirectory.Path));
	}

	[Fact]
	public void ResolveExecutablePath_WhenFirstProtectionFails_FallsBackToSecondCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstSourcePath = CreateSource(
			temporaryDirectory,
			"first-source.exe");
		string secondSourcePath = CreateSource(
			temporaryDirectory,
			"second-source.exe");
		FakeClaudeCliExecutableStager stager = new(sourcePath =>
		{
			if (string.Equals(
				sourcePath,
				firstSourcePath,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new ClaudeCliUntrustedException("Rejected test candidate.");
			}

			return sourcePath;
		});
		List<string> signaturePaths = new();
		List<string> versionPaths = new();
		WindowsOfficialCliExecutableValidator validator = new(
			"Anthropic, PBC",
			path =>
			{
				versionPaths.Add(path);
				return true;
			},
			path =>
			{
				signaturePaths.Add(path);
				return CreateSignature(ExactSignerSubject);
			},
			_ => true,
			_ => true,
			_ => true);

		string? result = ClaudeCliUsagePoller.ResolveExecutablePath(
			[firstSourcePath, secondSourcePath],
			validator,
			stager);

		Assert.Equal(Path.GetFullPath(secondSourcePath), result);
		Assert.Equal(
			[firstSourcePath, secondSourcePath],
			stager.ObservedSourcePaths);
		Assert.Empty(signaturePaths);
		Assert.Equal([Path.GetFullPath(secondSourcePath)], versionPaths);
	}

	private static string CreateSource(
		TemporaryDirectory temporaryDirectory,
		string fileName)
	{
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			fileName);
		File.WriteAllBytes(executablePath, [0x4D, 0x5A, 0x01, 0x02]);
		return executablePath;
	}

	private static void AddWritableCapabilityAce(string sourcePath)
	{
		FileInfo sourceFile = new(sourcePath);
		FileSecurity security = sourceFile.GetAccessControl(
			AccessControlSections.Access | AccessControlSections.Owner);
		security.AddAccessRule(new FileSystemAccessRule(
			new SecurityIdentifier(
				"S-1-15-3-600613-1-3-39304568-2807394902-4192062262"),
			FileSystemRights.FullControl,
			AccessControlType.Allow));
		sourceFile.SetAccessControl(security);
	}

	private static WindowsAuthenticodeInspection CreateSignature(
		string signerSubject)
	{
		return new WindowsAuthenticodeInspection(
			WinVerifyTrustStatus: 0,
			SignerSubject: signerSubject,
			SignerThumbprint: new string('A', 40));
	}
}
