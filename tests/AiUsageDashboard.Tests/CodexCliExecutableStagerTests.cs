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
		"CN=\"OpenAI OpCo, LLC\", O=\"OpenAI OpCo, LLC\", L=San Francisco, S=California, C=US";

	[Fact]
	public void Stage_WithOpenAiSignedSource_ProtectsOriginalPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "codex.exe");
		List<string> aclCheckedPaths = new();
		using CodexCliExecutableStager stager = new(
			_ => CreateSignature(ExactSignerSubject),
			_ => true,
			_ => true,
			path =>
			{
				aclCheckedPaths.Add(path);
				return true;
			});

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);

		Assert.True(lease.IsProtected);
		Assert.Equal(Path.GetFullPath(sourcePath), lease.ExecutablePath);
		Assert.NotEmpty(aclCheckedPaths);
		Assert.All(
			aclCheckedPaths,
			path => Assert.Equal(Path.GetFullPath(sourcePath), path));
		Assert.Equal([Path.GetFullPath(sourcePath)], Directory.EnumerateFiles(
			temporaryDirectory.Path));
	}

	[Fact]
	public void Stage_WithUnsafePhysicalSourceAcl_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory, "codex.exe");
		int signatureInspectionCount = 0;
		using CodexCliExecutableStager stager = new(
			_ =>
			{
				signatureInspectionCount++;
				return CreateSignature(ExactSignerSubject);
			},
			_ => true,
			_ => true,
			_ => false);

		Assert.Throws<CodexCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
		Assert.Equal(0, signatureInspectionCount);
	}

	[Fact]
	public void ResolveExecutablePath_WhenFirstProtectionFails_FallsBackToSecondCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string firstSourcePath = CreateSource(
			temporaryDirectory,
			"first-codex.exe");
		string secondSourcePath = CreateSource(
			temporaryDirectory,
			"second-codex.exe");
		FakeCodexCliExecutableStager stager = new(sourcePath =>
		{
			if (string.Equals(
				sourcePath,
				firstSourcePath,
				StringComparison.OrdinalIgnoreCase))
			{
				throw new CodexCliUntrustedException("Rejected test candidate.");
			}

			return sourcePath;
		});
		List<string> signaturePaths = new();
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
				signaturePaths.Add(path);
				return CreateSignature(ExactSignerSubject);
			},
			_ => true,
			_ => true,
			_ => true);

		string? result = CodexAppServerUsagePoller.ResolveExecutablePath(
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

	[Fact]
	public void ResolveDefaultProviderRoot_IsFixedDriveAndCurrentUserScoped()
	{
		string providerRoot = CodexCliExecutableStager.ResolveDefaultProviderRoot();
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
				$"AiUsageDashboard.CodexCli.{currentUserSid}"),
			providerRoot,
			ignoreCase: true);
		Assert.True(WindowsExecutablePathSecurity.IsFixedDrivePath(providerRoot));
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
