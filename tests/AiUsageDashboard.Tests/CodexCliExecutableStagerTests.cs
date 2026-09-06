using System.Security.Cryptography;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CodexCliExecutableStagerTests
{
	private sealed class FakeCodexCliExecutableStager :
		ICodexCliExecutableStager
	{
		private readonly Func<string, string> _stage;

		internal List<string> ObservedSourcePaths { get; } = new();

		internal FakeCodexCliExecutableStager(Func<string, string> stage)
		{
			_stage = stage;
		}

		public WindowsOfficialCliExecutableLease Stage(
			string sourceExecutablePath,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ObservedSourcePaths.Add(sourceExecutablePath);
			string stagedPath = _stage(sourceExecutablePath);
			return WindowsOfficialCliExecutableLease.CreateProtected(
				stagedPath,
				new FileStream(
					stagedPath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		}
	}

	private const string ExactSignerSubject =
		"CN=\"OpenAI OpCo, LLC\", O=\"OpenAI OpCo, LLC\", L=San Francisco, S=California, C=US";

	[Fact]
	public void Stage_WithOpenAiSignedSource_CreatesContentAddressedCopy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "codex.exe");
		string trustedRoot = Path.Combine(temporaryDirectory.Path, "trusted");
		List<string> aclCheckedPaths = new();
		using CodexCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateSignature(ExactSignerSubject),
			(path, root) =>
			{
				aclCheckedPaths.Add(path);
				return IsWithinRoot(path, root);
			});

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);
		string stagedPath = lease.ExecutablePath;
		Assert.True(lease.IsProtected);

		string expectedHash = Convert.ToHexString(
			SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
		Assert.Equal(
			Path.Combine(trustedRoot, $"codex-{expectedHash}.exe"),
			stagedPath,
			ignoreCase: true);
		Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(stagedPath));
		Assert.DoesNotContain(
			aclCheckedPaths,
			path => string.Equals(
				path,
				sourcePath,
				StringComparison.OrdinalIgnoreCase));
		Assert.All(
			aclCheckedPaths,
			path => Assert.True(IsWithinRoot(path, trustedRoot)));
	}

	[Fact]
	public void Stage_WithNonOpenAiSigner_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "codex.exe");
		string trustedRoot = Path.Combine(temporaryDirectory.Path, "trusted");
		using CodexCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateSignature("CN=Unexpected Publisher"));

		Assert.Throws<CodexCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
		Assert.Empty(Directory.EnumerateFiles(trustedRoot));
	}

	[Fact]
	public void Stage_WhenFinalProtectedCopyAclIsUnsafe_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "codex.exe");
		string trustedRoot = Path.Combine(temporaryDirectory.Path, "trusted");
		using CodexCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateSignature(ExactSignerSubject),
			(path, root) =>
				IsWithinRoot(path, root) &&
				!Path.GetFileName(path).StartsWith(
					"codex-",
					StringComparison.Ordinal));

		Assert.Throws<CodexCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
	}

	[Fact]
	public void ResolveExecutablePath_WhenFirstStageFails_FallsBackToSecondCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstSourcePath = CreateSource(
			temporaryDirectory,
			"first-codex.exe");
		string secondSourcePath = CreateSource(
			temporaryDirectory,
			"second-codex.exe");
		string stagedPath = CreateSource(
			temporaryDirectory,
			"protected-codex.exe");
		FakeCodexCliExecutableStager stager = new(sourcePath =>
		{
			if (string.Equals(
				sourcePath,
				firstSourcePath,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new CodexCliUntrustedException("Rejected test candidate.");
			}

			return stagedPath;
		});
		List<string> inspectedPaths = new();
		List<string> versionPaths = new();
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			path =>
			{
				versionPaths.Add(path);
				return true;
			},
			path =>
			{
				inspectedPaths.Add(path);
				return CreateSignature(ExactSignerSubject);
			},
			_ => true,
			_ => true,
			_ => true);

		string? result = CodexAppServerUsagePoller.ResolveExecutablePath(
			[firstSourcePath, secondSourcePath],
			validator,
			stager);

		Assert.Equal(Path.GetFullPath(stagedPath), result);
		Assert.Equal(
			[firstSourcePath, secondSourcePath],
			stager.ObservedSourcePaths);
		Assert.Empty(inspectedPaths);
		Assert.Equal([Path.GetFullPath(stagedPath)], versionPaths);
	}

	[Fact]
	public void ResolveDefaultTrustedRoot_IsFixedDriveAndCurrentUserScoped()
	{
		string trustedRoot = CodexCliExecutableStager.ResolveDefaultTrustedRoot();
		string fixedDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ??
			throw new InvalidOperationException(
				"The Windows system drive is unavailable.");
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		string currentUserSid = identity.User?.Value ??
			throw new InvalidOperationException(
				"The current Windows identity has no user SID.");

		Assert.Equal(
			Path.Combine(
				fixedDriveRoot,
				$"AiUsageDashboard.CodexCli.{currentUserSid}",
				"executables-v1"),
			trustedRoot,
			ignoreCase: true);
		Assert.True(WindowsExecutablePathSecurity.IsFixedDrivePath(trustedRoot));
	}

	private static CodexCliExecutableStager CreateStager(
		string trustedRoot,
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, string, bool>? isPathAclSafe = null)
	{
		return new CodexCliExecutableStager(
			() => trustedRoot,
			inspectSignature,
			File.Exists,
			_ => true,
			isPathAclSafe ?? ((_, _) => true),
			(_, _) => true,
			path =>
			{
				Directory.CreateDirectory(path);
				return true;
			},
			File.Exists);
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

	private static WindowsAuthenticodeInspection CreateSignature(
		string signerSubject)
	{
		return new WindowsAuthenticodeInspection(
			WinVerifyTrustStatus: 0,
			SignerSubject: signerSubject,
			SignerThumbprint: new string('A', 40));
	}

	private static bool IsWithinRoot(string path, string root)
	{
		string normalizedRoot = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(root));
		string fullPath = Path.GetFullPath(path);
		return fullPath.StartsWith(
			$"{normalizedRoot}{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase);
	}
}
