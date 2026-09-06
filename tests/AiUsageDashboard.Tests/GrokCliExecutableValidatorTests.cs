using System.Security.AccessControl;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class GrokCliExecutableValidatorTests
{
	private sealed class FakeExecutableStager : IGrokCliExecutableStager
	{
		private readonly string _stagedPath;

		internal string? ObservedSourcePath { get; private set; }

		internal FakeExecutableStager(string stagedPath)
		{
			_stagedPath = stagedPath;
		}

		public WindowsOfficialCliExecutableLease Stage(
			string sourceExecutablePath,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ObservedSourcePath = sourceExecutablePath;
			FileStream stream = new(
				_stagedPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);
			return WindowsOfficialCliExecutableLease.CreateProtected(
				_stagedPath,
				stream);
		}
	}

	private sealed class FakeVersionProbe : IGrokCliVersionProbe
	{
		private readonly GrokExecutableVersion? _version;

		internal int CallCount { get; private set; }

		internal string? ObservedExecutablePath { get; private set; }

		internal ProviderProcessOperationTracker? ObservedOperationTracker
		{
			get;
			private set;
		}

		internal FakeVersionProbe(GrokExecutableVersion? version)
		{
			_version = version;
		}

		public Task<GrokExecutableVersion?> ReadVersionAsync(
			string executablePath,
			CancellationToken cancellationToken = default,
			ProviderProcessOperationTracker? operationTracker = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;
			ObservedExecutablePath = executablePath;
			ObservedOperationTracker = operationTracker;
			return Task.FromResult(_version);
		}
	}

	private const string ExactSignerSubject =
		"CN=X.AI LLC, O=X.AI LLC, L=Palo Alto, S=California, C=US";

	[Fact]
	public async Task Validate_WithExactSignerAndReferenceVersion_ReturnsEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			ExactSignerSubject,
			new GrokExecutableVersion(1, 0, 3));

		using GrokValidatedExecutable result =
			await validator.ResolveAndValidateAsync();

		Assert.Equal(Path.GetFullPath(executablePath), result.ExecutablePath);
		Assert.Equal("1.0.3", result.VersionEvidence.DetectedVersion);
		Assert.Equal(
			CliVersionDisposition.Reference,
			result.VersionEvidence.Disposition);
	}

	[Fact]
	public async Task Resolve_WithStager_UsesProtectedLeaseAndValidatesStagedPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.Combine(
			temporaryDirectory.Path,
			"official-source.exe");
		string stagedPath = Path.Combine(
			temporaryDirectory.Path,
			"protected-copy.exe");
		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x01]);
		File.WriteAllBytes(stagedPath, [0x4D, 0x5A, 0x02]);
		FakeExecutableStager stager = new(stagedPath);
		string? inspectedPath = null;
		string? versionPath = null;
		GrokCliExecutableValidator validator = new(
			() => sourcePath,
			path =>
			{
				inspectedPath = path;
				return new WindowsAuthenticodeInspection(
					WinVerifyTrustStatus: 0,
					SignerSubject: ExactSignerSubject,
					SignerThumbprint: new string('A', 40));
			},
			path =>
			{
				versionPath = path;
				return new GrokExecutableVersion(1, 0, 3);
			},
			new FakeVersionProbe(new GrokExecutableVersion(1, 0, 3)),
			_ => true,
			path => string.Equals(
				path,
				stagedPath,
				StringComparison.OrdinalIgnoreCase),
			stager);

		using GrokValidatedExecutable result =
			await validator.ResolveAndValidateAsync();

		Assert.Equal(sourcePath, stager.ObservedSourcePath);
		Assert.Null(inspectedPath);
		Assert.Equal(Path.GetFullPath(stagedPath), versionPath);
		Assert.Equal(Path.GetFullPath(stagedPath), result.ExecutablePath);
		Assert.True(result.ExecutableLease?.IsProtected);
	}

	[Fact]
	public async Task Validate_WithUnverifiedNewerVersion_ReturnsEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			ExactSignerSubject,
			new GrokExecutableVersion(1, 1, 0));

		using GrokValidatedExecutable result =
			await validator.ValidateAsync(executablePath);

		Assert.Equal(Path.GetFullPath(executablePath), result.ExecutablePath);
		Assert.Equal("1.1.0", result.VersionEvidence.DetectedVersion);
		Assert.Equal(
			CliVersionDisposition.UnverifiedAllowed,
			result.VersionEvidence.Disposition);
	}

	[Fact]
	public async Task Validate_WithUnverifiedOlderVersion_ReturnsEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			ExactSignerSubject,
			new GrokExecutableVersion(1, 0, 2));

		using GrokValidatedExecutable result =
			await validator.ValidateAsync(executablePath);

		Assert.Equal(Path.GetFullPath(executablePath), result.ExecutablePath);
		Assert.Equal("1.0.2", result.VersionEvidence.DetectedVersion);
		Assert.Equal(
			CliVersionDisposition.UnverifiedAllowed,
			result.VersionEvidence.Disposition);
	}

	[Fact]
	public async Task Validate_WhenCalledTwice_PerformsFullInspectionTwice()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		int signatureInspectionCount = 0;
		int versionInspectionCount = 0;
		GrokCliExecutableValidator validator = new(
			() => executablePath,
			_ =>
			{
				signatureInspectionCount++;
				return new WindowsAuthenticodeInspection(
					WinVerifyTrustStatus: 0,
					SignerSubject: ExactSignerSubject,
					SignerThumbprint: new string('A', 40));
			},
			_ =>
			{
				versionInspectionCount++;
				return new GrokExecutableVersion(1, 0, 3);
			},
			new FakeVersionProbe(new GrokExecutableVersion(1, 0, 3)),
			_ => true,
			_ => true);

		using GrokValidatedExecutable firstResult =
			await validator.ValidateAsync(executablePath);
		using GrokValidatedExecutable secondResult =
			await validator.ValidateAsync(executablePath);

		Assert.Equal(2, signatureInspectionCount);
		Assert.Equal(2, versionInspectionCount);
	}

	[Fact]
	public async Task Validate_WhenEmbeddedVersionIsMissing_UsesContainedProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeVersionProbe probe = new(new GrokExecutableVersion(1, 0, 4));
		ProviderProcessOperationTracker operationTracker = new();
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			ExactSignerSubject,
			version: null,
			probe: probe);

		using GrokValidatedExecutable result = await validator.ValidateAsync(
			executablePath,
			operationTracker: operationTracker);

		Assert.Equal(Path.GetFullPath(executablePath), result.ExecutablePath);
		Assert.Equal("1.0.4", result.VersionEvidence.DetectedVersion);
		Assert.Equal(1, probe.CallCount);
		Assert.Equal(Path.GetFullPath(executablePath), probe.ObservedExecutablePath);
		Assert.Same(operationTracker, probe.ObservedOperationTracker);
		Assert.True(result.ExecutableLease?.IsProtected);
	}

	[Fact]
	public async Task Validate_WhenContainedProbeReportsUnverifiedOlderVersion_ReturnsEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeVersionProbe probe = new(new GrokExecutableVersion(1, 0, 2));
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			ExactSignerSubject,
			version: null,
			probe: probe);

		using GrokValidatedExecutable result =
			await validator.ValidateAsync(executablePath);

		Assert.Equal(Path.GetFullPath(executablePath), result.ExecutablePath);
		Assert.Equal("1.0.2", result.VersionEvidence.DetectedVersion);
		Assert.Equal(
			CliVersionDisposition.UnverifiedAllowed,
			result.VersionEvidence.Disposition);
		Assert.Equal(1, probe.CallCount);
	}

	[Fact]
	public async Task Validate_WhenContainedProbeCannotParseVersion_ReturnsUnknownEvidence()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeVersionProbe probe = new(version: null);
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			ExactSignerSubject,
			version: null,
			probe: probe);

		using GrokValidatedExecutable result =
			await validator.ValidateAsync(executablePath);

		Assert.Equal(Path.GetFullPath(executablePath), result.ExecutablePath);
		Assert.Null(result.VersionEvidence.DetectedVersion);
		Assert.Equal(
			CliVersionDisposition.UnknownAllowed,
			result.VersionEvidence.Disposition);
		Assert.Equal(1, probe.CallCount);
	}

	[Theory]
	[InlineData("CN=X.AI LLC Malware, O=X.AI LLC, C=US")]
	[InlineData("CN=Not X.AI LLC, O=X.AI LLC, C=US")]
	[InlineData("O=X.AI LLC, C=US")]
	[InlineData("")]
	public async Task Validate_WithSimilarOrMissingSignerName_RejectsExecutable(
		string signerSubject)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			signerSubject,
			new GrokExecutableVersion(1, 0, 3));

		await Assert.ThrowsAsync<GrokCliUntrustedException>(
			() => validator.ValidateAsync(executablePath));
	}

	[Fact]
	public async Task Validate_WithUntrustedSigner_DoesNotExecuteVersionProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeVersionProbe probe = new(new GrokExecutableVersion(1, 0, 4));
		GrokCliExecutableValidator validator = CreateValidator(
			executablePath,
			"CN=Not X.AI LLC, O=Unknown",
			version: null,
			probe: probe);

		await Assert.ThrowsAsync<GrokCliUntrustedException>(
			() => validator.ValidateAsync(executablePath));

		Assert.Equal(0, probe.CallCount);
	}

	[Theory]
	[InlineData("CN=X.AI LLC, O=X.AI LLC")]
	[InlineData("O=X.AI LLC, CN=\"X.AI LLC\", C=US")]
	[InlineData("cn=x.ai llc, O=X.AI LLC")]
	public void HasExpectedSigner_WithExactCommonName_AcceptsDistinguishedName(
		string signerSubject)
	{
		Assert.True(GrokCliExecutableValidator.HasExpectedSigner(signerSubject));
	}

	[Theory]
	[InlineData("CN=Other, O=\"Acme, CN=X.AI LLC\"")]
	[InlineData("CN=X.AI LLC Malware, O=X.AI LLC")]
	[InlineData("CN=X.AI, O=X.AI LLC")]
	[InlineData("O=X.AI LLC")]
	[InlineData(null)]
	public void HasExpectedSigner_WithoutExactCommonName_RejectsDistinguishedName(
		string? signerSubject)
	{
		Assert.False(GrokCliExecutableValidator.HasExpectedSigner(signerSubject));
	}

	[Fact]
	public void IsAccessControlSafe_WithUntrustedTraverseAndSynchronize_AcceptsAcl()
	{
		FileSecurity security = CreateSecurityWithUntrustedAccess(
			FileSystemRights.Traverse | FileSystemRights.Synchronize);

		Assert.True(GrokCliExecutableValidator.IsAccessControlSafe(security));
	}

	[Fact]
	public void IsAccessControlSafe_WithUntrustedWriteData_RejectsAcl()
	{
		FileSecurity security = CreateSecurityWithUntrustedAccess(
			FileSystemRights.WriteData);

		Assert.False(GrokCliExecutableValidator.IsAccessControlSafe(security));
	}

	private static GrokCliExecutableValidator CreateValidator(
		string executablePath,
		string signerSubject,
		GrokExecutableVersion? version,
		FakeVersionProbe? probe = null)
	{
		return new GrokCliExecutableValidator(
			() => executablePath,
			_ => new WindowsAuthenticodeInspection(
				WinVerifyTrustStatus: 0,
				SignerSubject: signerSubject,
				SignerThumbprint: new string('A', 40)),
			_ => version,
			probe ?? new FakeVersionProbe(version),
			_ => true,
			_ => true);
	}

	private static string CreateExecutable(TemporaryDirectory temporaryDirectory)
	{
		string executablePath = Path.Combine(temporaryDirectory.Path, "grok.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		return executablePath;
	}

	private static FileSecurity CreateSecurityWithUntrustedAccess(
		FileSystemRights rights)
	{
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		SecurityIdentifier owner = identity.User ??
			throw new InvalidOperationException(
				"The current Windows identity has no user SID.");
		SecurityIdentifier untrustedIdentity = new(
			WellKnownSidType.WorldSid,
			null);
		FileSecurity security = new();
		security.SetOwner(owner);
		security.AddAccessRule(new FileSystemAccessRule(
			untrustedIdentity,
			rights,
			AccessControlType.Allow));
		return security;
	}
}
