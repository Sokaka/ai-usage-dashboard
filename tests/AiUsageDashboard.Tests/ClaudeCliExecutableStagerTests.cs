using System.Security.Cryptography;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class ClaudeCliExecutableStagerTests
{
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
		"CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", L=San Francisco, S=California, C=US";

	[Fact]
	public void Stage_WithAnthropicSignedSource_CreatesContentAddressedCopy()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "claude.exe");
		string trustedRoot = Path.Combine(temporaryDirectory.Path, "trusted");
		using ClaudeCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateSignature(ExactSignerSubject));

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);
		string stagedPath = lease.ExecutablePath;
		Assert.True(lease.IsProtected);

		string expectedHash = Convert.ToHexString(
			SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
		Assert.Equal(
			Path.Combine(trustedRoot, $"claude-{expectedHash}.exe"),
			stagedPath,
			ignoreCase: true);
		Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(stagedPath));
	}

	[Fact]
	public void Stage_WithNonAnthropicSigner_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "claude.exe");
		string trustedRoot = Path.Combine(temporaryDirectory.Path, "trusted");
		using ClaudeCliExecutableStager stager = CreateStager(
			trustedRoot,
			_ => CreateSignature("CN=X.AI LLC, O=X.AI LLC"));

		Assert.Throws<ClaudeCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
		Assert.Empty(Directory.EnumerateFiles(trustedRoot));
	}

	[Fact]
	public void ResolveExecutablePath_WhenFirstStageFails_FallsBackToSecondCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstSourcePath = CreateSource(
			temporaryDirectory,
			"first-source.exe");
		string secondSourcePath = CreateSource(
			temporaryDirectory,
			"second-source.exe");
		string stagedPath = CreateSource(
			temporaryDirectory,
			"protected-claude.exe");
		FakeClaudeCliExecutableStager stager = new(sourcePath =>
		{
			if (string.Equals(
				sourcePath,
				firstSourcePath,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new ClaudeCliUntrustedException("Rejected test candidate.");
			}

			return stagedPath;
		});
		List<string> inspectedPaths = new();
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
				inspectedPaths.Add(path);
				return CreateSignature(ExactSignerSubject);
			},
			_ => true,
			_ => true,
			_ => true);

		string? result = ClaudeCliUsagePoller.ResolveExecutablePath(
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
		string trustedRoot = ClaudeCliExecutableStager.ResolveDefaultTrustedRoot();
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
				$"AiUsageDashboard.ClaudeCli.{currentUserSid}",
				"executables-v1"),
			trustedRoot,
			ignoreCase: true);
	}

	private static ClaudeCliExecutableStager CreateStager(
		string trustedRoot,
		Func<string, WindowsAuthenticodeInspection> inspectSignature)
	{
		return new ClaudeCliExecutableStager(
			() => trustedRoot,
			inspectSignature,
			File.Exists,
			_ => true,
			(_, _) => true,
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
}
