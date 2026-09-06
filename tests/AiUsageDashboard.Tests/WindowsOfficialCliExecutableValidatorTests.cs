using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class WindowsOfficialCliExecutableValidatorTests
{
	private sealed class CountingCodexExecutableStager :
		ICodexCliExecutableStager
	{
		private int _stageCallCount;

		internal int StageCallCount => Volatile.Read(ref _stageCallCount);

		public WindowsOfficialCliExecutableLease Stage(
			string sourceExecutablePath,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _stageCallCount);
			return WindowsOfficialCliExecutableLease.CreateUnprotected(
				sourceExecutablePath);
		}
	}

	private sealed class PrivateFixedDriveTemporaryDirectory : IDisposable
	{
		private const string DirectoryNamePrefix =
			"AiUsageDashboard.CodexAclTests.";

		internal string Path { get; }

		internal PrivateFixedDriveTemporaryDirectory()
		{
			List<string> failures = new();
			string[] candidateRoots =
			[
				System.IO.Path.GetPathRoot(Environment.SystemDirectory) ??
					string.Empty,
				System.IO.Path.GetPathRoot(AppContext.BaseDirectory) ??
					string.Empty
			];

			foreach (string candidateRoot in candidateRoots
				.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				if (string.IsNullOrWhiteSpace(candidateRoot) ||
					!WindowsExecutablePathSecurity.IsFixedDrivePath(
						candidateRoot))
				{
					continue;
				}

				string candidatePath = System.IO.Path.Combine(
					candidateRoot,
					$"{DirectoryNamePrefix}{Guid.NewGuid():N}");

				if (!TryCreatePrivateDirectory(candidatePath))
				{
					failures.Add(
						$"{candidateRoot}: private directory creation failed");
					continue;
				}

				if (WindowsExecutablePathSecurity
					.IsDirectoryPathAclSafeWithinTrustedRoot(
						candidatePath,
						candidatePath))
				{
					Path = candidatePath;
					return;
				}

				DeleteCandidateDirectory(candidatePath);
				failures.Add(
					$"{candidateRoot}: trusted-root ACL contract rejected");
			}

			throw new InvalidOperationException(
				"A trusted private test directory could not be created on a " +
				"fixed drive. " +
				string.Join("; ", failures));
		}

		public void Dispose()
		{
			if (!Directory.Exists(Path))
			{
				return;
			}

			string fullPath = System.IO.Path.GetFullPath(Path);
			string? parentPath = System.IO.Path.GetDirectoryName(fullPath);
			string? volumeRoot = System.IO.Path.GetPathRoot(fullPath);

			if (string.IsNullOrWhiteSpace(parentPath) ||
				string.IsNullOrWhiteSpace(volumeRoot) ||
				!string.Equals(
					System.IO.Path.TrimEndingDirectorySeparator(parentPath),
					System.IO.Path.TrimEndingDirectorySeparator(volumeRoot),
					StringComparison.OrdinalIgnoreCase) ||
				!System.IO.Path.GetFileName(fullPath).StartsWith(
					DirectoryNamePrefix,
					StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					"Refusing to remove an unexpected ACL test directory.");
			}

			DeleteCandidateDirectory(fullPath);
		}

		internal static bool TryCreatePrivateDirectory(string directoryPath)
		{
			try
			{
				DirectoryInfo directory = Directory.CreateDirectory(directoryPath);
				using WindowsIdentity identity = WindowsIdentity.GetCurrent();
				SecurityIdentifier currentUser = identity.User ??
					throw new InvalidOperationException(
						"The current Windows identity has no user SID.");
				SecurityIdentifier[] allowedIdentities =
				[
					currentUser,
					new SecurityIdentifier(
						WellKnownSidType.LocalSystemSid,
						null),
					new SecurityIdentifier(
						WellKnownSidType.BuiltinAdministratorsSid,
						null)
				];
				DirectorySecurity security = directory.GetAccessControl(
					AccessControlSections.Access |
						AccessControlSections.Owner);
				security.SetAccessRuleProtection(
					isProtected: true,
					preserveInheritance: false);

				foreach (SecurityIdentifier allowedIdentity in allowedIdentities)
				{
					security.AddAccessRule(new FileSystemAccessRule(
						allowedIdentity,
						FileSystemRights.FullControl,
						InheritanceFlags.ContainerInherit |
							InheritanceFlags.ObjectInherit,
						PropagationFlags.None,
						AccessControlType.Allow));
				}

				directory.SetAccessControl(security);
				return AntigravityPrivateKeyAcl.IsPrivateDirectory(
					directory.FullName);
			}
			catch
			{
				try
				{
					if (Directory.Exists(directoryPath))
					{
						Directory.Delete(directoryPath, recursive: false);
					}
				}
				catch
				{
					// A failed fixture setup is reported by the caller.
				}

				return false;
			}
		}

		private static void DeleteCandidateDirectory(string directoryPath)
		{
			Directory.Delete(directoryPath, recursive: true);
		}
	}

	private sealed record OfficialJunctionLayout(
		string CodexHome,
		string CurrentPath,
		string PhysicalExecutablePath,
		string StandaloneRoot,
		string VisibleBinPath,
		string VisibleExecutablePath);

	private const string AnthropicSignerSubject =
		"CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", L=San Francisco, S=California, C=US";
	private const string OpenAiSignerSubject =
		"CN=\"OpenAI OpCo, LLC\", O=\"OpenAI OpCo, LLC\", L=San Francisco, S=California, C=US";

	[Fact]
	public void Validate_WithTrustedExecutable_ReturnsCanonicalPath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"claude.exe");
		WindowsOfficialCliExecutableValidator validator = CreateValidator(
			"Anthropic, PBC",
			AnthropicSignerSubject);

		string result = validator.Validate(executablePath);

		Assert.Equal(Path.GetFullPath(executablePath), result);
	}

	[Fact]
	public void ValidateStaged_WithUnprotectedLease_RejectsBeforeValidation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"claude.exe");
		using WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateUnprotected(executablePath);
		WindowsOfficialCliExecutableValidator validator = CreateValidator(
			"Anthropic, PBC",
			AnthropicSignerSubject);

		OfficialCliExecutableValidationException exception = Assert.Throws<
			OfficialCliExecutableValidationException>(
				() => validator.ValidateStaged(executableLease));

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason.UnsafePath,
			exception.FailureReason);
	}

	[Fact]
	public void ValidateStaged_WithProtectedLease_SkipsRedundantSignatureInspection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"claude.exe");
		int signatureInspectionCount = 0;
		WindowsOfficialCliExecutableValidator validator = new(
			"Anthropic, PBC",
			_ => true,
			_ =>
			{
				Interlocked.Increment(ref signatureInspectionCount);
				return CreateSignature(0, AnthropicSignerSubject);
			},
			_ => true,
			_ => true,
			_ => true);
		FileStream stream = new(
			executablePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		using WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				stream);

		string result = validator.ValidateStaged(executableLease);

		Assert.Equal(Path.GetFullPath(executablePath), result);
		Assert.Equal(0, Volatile.Read(ref signatureInspectionCount));
	}

	[Theory]
	[InlineData(1, AnthropicSignerSubject)]
	[InlineData(0, "CN=Anthropic PBC, O=Anthropic PBC")]
	[InlineData(0, "O=\"Anthropic, PBC\", C=US")]
	public void Validate_WithInvalidSignature_RejectsExecutable(
		int trustStatus,
		string signerSubject)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"claude.exe");
		WindowsOfficialCliExecutableValidator validator = CreateValidator(
			"Anthropic, PBC",
			signerSubject,
			trustStatus: trustStatus);

		OfficialCliExecutableValidationException exception = Assert.Throws<
			OfficialCliExecutableValidationException>(
				() => validator.Validate(executablePath));

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason.InvalidSignature,
			exception.FailureReason);
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void Validate_WithUnsafePath_RejectsBeforeSignatureInspection(
		bool isCanonicalNonReparseFile,
		bool isPathAclSafe)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		int signatureInspectionCount = 0;
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			_ => true,
			_ =>
			{
				signatureInspectionCount++;
				return CreateSignature(0, OpenAiSignerSubject);
			},
			_ => isCanonicalNonReparseFile,
			_ => true,
			_ => isPathAclSafe);

		OfficialCliExecutableValidationException exception = Assert.Throws<
			OfficialCliExecutableValidationException>(
				() => validator.Validate(executablePath));

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason.UnsafePath,
			exception.FailureReason);
		Assert.Equal(0, signatureInspectionCount);
	}

	[Fact]
	public void Validate_WithUnexpectedVersion_RejectsExecutable()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		WindowsOfficialCliExecutableValidator validator = CreateValidator(
			"OpenAI OpCo, LLC",
			OpenAiSignerSubject,
			hasExpectedVersion: false);

		OfficialCliExecutableValidationException exception = Assert.Throws<
			OfficialCliExecutableValidationException>(
				() => validator.Validate(executablePath));

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason.InvalidVersion,
			exception.FailureReason);
	}

	[Fact]
	public void Validate_WhenVersionProbeFailsOperationally_PreservesRetryableFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		TimeoutException probeFailure = new("Version probe timed out.");
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			_ => throw probeFailure,
			_ => CreateSignature(0, OpenAiSignerSubject),
			_ => true,
			_ => true,
			_ => true);

		OfficialCliExecutableValidationException exception = Assert.Throws<
			OfficialCliExecutableValidationException>(
				() => validator.Validate(executablePath));

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason.VersionProbeFailed,
			exception.FailureReason);
		Assert.Same(probeFailure, exception.InnerException);
	}

	[Fact]
	public void Validate_WhenVersionProbeContainmentFails_PreservesRestartFailure()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		CodexCliVersionProbeContainmentException containmentFailure = new(
			"Containment failed.");
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			_ => throw containmentFailure,
			_ => CreateSignature(0, OpenAiSignerSubject),
			_ => true,
			_ => true,
			_ => true);

		OfficialCliExecutableValidationException exception = Assert.Throws<
			OfficialCliExecutableValidationException>(
				() => validator.Validate(executablePath));

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason.VersionProbeContainmentFailed,
			exception.FailureReason);
		Assert.Same(containmentFailure, exception.InnerException);
	}

	[Fact]
	public void ClaudeResolver_WithUntrustedThenTrustedCandidate_UsesTrustedCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string untrustedPath = CreateExecutable(
			temporaryDirectory,
			"untrusted-claude.exe");
		string trustedPath = CreateExecutable(
			temporaryDirectory,
			"trusted-claude.exe");
		WindowsOfficialCliExecutableValidator validator = new(
			"Anthropic, PBC",
			_ => true,
			path => CreateSignature(
				0,
				string.Equals(
					path,
					trustedPath,
					StringComparison.OrdinalIgnoreCase)
					? AnthropicSignerSubject
					: "CN=Unexpected Publisher"),
			_ => true,
			_ => true,
			_ => true);

		string? result = ClaudeCliUsagePoller.ResolveExecutablePath(
			[untrustedPath, trustedPath],
			validator);

		Assert.Equal(Path.GetFullPath(trustedPath), result);
	}

	[Fact]
	public void ClaudeOfficialPathPolicy_WithAclSafeOffProfileCandidate_AllowsResolvedExecutableValidation()
	{
		using PrivateFixedDriveTemporaryDirectory temporaryDirectory = new();
		string installDirectory = Path.Combine(
			temporaryDirectory.Path,
			"claude-install");
		Directory.CreateDirectory(installDirectory);
		string executablePath = Path.Combine(
			installDirectory,
			"claude.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		WindowsOfficialCliExecutableValidator validator =
			ClaudeCliUsagePoller.CreateOfficialExecutableValidator(
				_ => true,
				_ => CreateSignature(0, AnthropicSignerSubject));

		string? result = ClaudeCliUsagePoller.ResolveExecutablePath(
			[executablePath],
			validator);

		Assert.False(WindowsExecutablePathSecurity.IsPathAclSafe(
			executablePath));
		Assert.Equal(Path.GetFullPath(executablePath), result);
	}

	[Fact]
	public void ClaudeOfficialPathPolicy_WithWritableAncestor_RejectsBeforeSignatureInspection()
	{
		using PrivateFixedDriveTemporaryDirectory temporaryDirectory = new();
		string unsafeParentPath = Path.Combine(
			temporaryDirectory.Path,
			"unsafe-parent");
		DirectoryInfo unsafeParent = Directory.CreateDirectory(unsafeParentPath);
		DirectorySecurity unsafeParentSecurity = unsafeParent.GetAccessControl(
			AccessControlSections.Access |
				AccessControlSections.Owner);
		unsafeParentSecurity.AddAccessRule(new FileSystemAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
			FileSystemRights.WriteAttributes,
			AccessControlType.Allow));
		unsafeParent.SetAccessControl(unsafeParentSecurity);
		string installDirectory = Path.Combine(
			unsafeParentPath,
			"claude-install");
		Assert.True(PrivateFixedDriveTemporaryDirectory
			.TryCreatePrivateDirectory(installDirectory));
		string executablePath = Path.Combine(
			installDirectory,
			"claude.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		int signatureInspectionCount = 0;
		WindowsOfficialCliExecutableValidator validator =
			ClaudeCliUsagePoller.CreateOfficialExecutableValidator(
				_ => true,
				_ =>
				{
					signatureInspectionCount++;
					return CreateSignature(0, AnthropicSignerSubject);
				});

		ClaudeCliUntrustedException exception = Assert.Throws<
			ClaudeCliUntrustedException>(() =>
				ClaudeCliUsagePoller.ResolveExecutablePath(
					[executablePath],
					validator));
		OfficialCliExecutableValidationException validationFailure =
			Assert.IsType<OfficialCliExecutableValidationException>(
				exception.InnerException);

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason.UnsafePath,
			validationFailure.FailureReason);
		Assert.Equal(0, signatureInspectionCount);
	}

	[Fact]
	public void CodexResolver_WithOnlyUntrustedCandidate_ThrowsActionableException()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		WindowsOfficialCliExecutableValidator validator = CreateValidator(
			"OpenAI OpCo, LLC",
			"CN=Unexpected Publisher");

		CodexCliUntrustedException exception = Assert.Throws<
			CodexCliUntrustedException>(() =>
				CodexAppServerUsagePoller.ResolveExecutablePath(
					[executablePath],
					validator));

		Assert.Contains("OpenAI 官方來源", exception.Message);
	}

	[Fact]
	public void CodexResolver_WhenVersionProbeFails_ThrowsRetryableException()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		CodexCliVersionProbeException probeFailure = new(
			"Probe failed.",
			new TimeoutException("Probe timed out."));
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			_ => throw probeFailure,
			_ => CreateSignature(0, OpenAiSignerSubject),
			_ => true,
			_ => true,
			_ => true);

		CodexCliProbeException exception = Assert.Throws<
			CodexCliProbeException>(() =>
				CodexAppServerUsagePoller.ResolveExecutablePath(
					[executablePath],
					validator));

		Assert.Contains("稍後再試", exception.Message, StringComparison.Ordinal);
		OfficialCliExecutableValidationException validationFailure =
			Assert.IsType<OfficialCliExecutableValidationException>(
				exception.InnerException);
		Assert.Same(probeFailure, validationFailure.InnerException);
	}

	[Fact]
	public void CodexResolver_WhenVersionProbeContainmentFails_RequiresRestart()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		CodexCliVersionProbeContainmentException containmentFailure = new(
			"Containment failed.");
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			_ => throw containmentFailure,
			_ => CreateSignature(0, OpenAiSignerSubject),
			_ => true,
			_ => true,
			_ => true);

		CodexCliContainmentException exception = Assert.Throws<
			CodexCliContainmentException>(() =>
				CodexAppServerUsagePoller.ResolveExecutablePath(
					[executablePath],
					validator));

		Assert.Contains("重新啟動", exception.Message, StringComparison.Ordinal);
		OfficialCliExecutableValidationException validationFailure =
			Assert.IsType<OfficialCliExecutableValidationException>(
				exception.InnerException);
		Assert.Same(containmentFailure, validationFailure.InnerException);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task CodexOfficialPathResolver_WithCustomVisibleJunction_ReturnsPhysicalExecutable(
		bool isPackageLayout)
	{
		using TemporaryDirectory temporaryDirectory = new();
		OfficialJunctionLayout layout = await CreateOfficialJunctionLayoutAsync(
			temporaryDirectory,
			isPackageLayout);

		try
		{
			string result = ResolveOfficialLayout(layout);
			string stagingSource = ResolveOfficialStagingSourceLayout(layout);

			Assert.Equal(layout.PhysicalExecutablePath, result);
			Assert.Equal(layout.PhysicalExecutablePath, stagingSource);
		}
		finally
		{
			DeleteJunction(layout.VisibleBinPath);
			DeleteJunction(layout.CurrentPath);
		}
	}

	[Fact]
	public async Task CodexOfficialStagingSource_WithUnsafeSourceAcl_ReturnsPhysicalExecutable()
	{
		using PrivateFixedDriveTemporaryDirectory temporaryDirectory = new();
		string unsafeParentPath = Path.Combine(
			temporaryDirectory.Path,
			"unsafe-source-parent");
		DirectoryInfo unsafeParent = Directory.CreateDirectory(unsafeParentPath);
		DirectorySecurity unsafeParentSecurity = unsafeParent.GetAccessControl(
			AccessControlSections.Access |
				AccessControlSections.Owner);
		unsafeParentSecurity.AddAccessRule(new FileSystemAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
			FileSystemRights.WriteAttributes,
			AccessControlType.Allow));
		unsafeParent.SetAccessControl(unsafeParentSecurity);
		OfficialJunctionLayout layout = await CreateOfficialJunctionLayoutAsync(
			unsafeParentPath,
			isPackageLayout: true);

		try
		{
			Assert.False(WindowsExecutablePathSecurity.IsPathAclSafe(
				layout.PhysicalExecutablePath));

			string stagingSource = ResolveOfficialStagingSourceLayout(layout);

			Assert.Equal(layout.PhysicalExecutablePath, stagingSource);
		}
		finally
		{
			DeleteJunction(layout.VisibleBinPath);
			DeleteJunction(layout.CurrentPath);
		}
	}

	[Fact]
	public async Task CodexOfficialPathPolicy_WithAclSafeOffProfileRoots_AllowsResolvedExecutableValidation()
	{
		using PrivateFixedDriveTemporaryDirectory temporaryDirectory = new();
		OfficialJunctionLayout layout = await CreateOfficialJunctionLayoutAsync(
			temporaryDirectory.Path,
			isPackageLayout: true);

		try
		{
			string resolvedPath = CodexOfficialExecutablePathResolver.Resolve(
				layout.VisibleExecutablePath,
				layout.VisibleBinPath,
				layout.CodexHome,
				"x86_64-pc-windows-msvc");
			string defaultVisibleBinDirectory = System.IO.Path.Combine(
				temporaryDirectory.Path,
				"unused-default-bin");
			WindowsOfficialCliExecutableValidator validator = new(
				"OpenAI OpCo, LLC",
				_ => true,
				_ => CreateSignature(0, OpenAiSignerSubject),
				WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
				WindowsExecutablePathSecurity.IsFixedDrivePath,
				path => CodexOfficialExecutablePathResolver
					.IsResolvedExecutablePathAclSafe(
						path,
						defaultVisibleBinDirectory,
						layout.VisibleBinPath,
						layout.CodexHome));

			string validatedPath = validator.Validate(resolvedPath);

			Assert.False(WindowsExecutablePathSecurity.IsPathAclSafe(
				resolvedPath));
			Assert.Equal(layout.PhysicalExecutablePath, validatedPath);
			string unrelatedDirectory = System.IO.Path.Combine(
				temporaryDirectory.Path,
				"unrelated");
			Directory.CreateDirectory(unrelatedDirectory);
			string unrelatedExecutablePath = System.IO.Path.Combine(
				unrelatedDirectory,
				"codex.exe");
			File.WriteAllBytes(unrelatedExecutablePath, [0x4D, 0x5A]);
			Assert.False(CodexOfficialExecutablePathResolver
				.IsResolvedExecutablePathAclSafe(
					unrelatedExecutablePath,
					defaultVisibleBinDirectory,
					layout.VisibleBinPath,
					layout.CodexHome));
		}
		finally
		{
			DeleteJunction(layout.VisibleBinPath);
			DeleteJunction(layout.CurrentPath);
		}
	}

	[Fact]
	public void WindowsExecutablePathSecurity_WithMutableTrustedRootAncestor_RejectsPath()
	{
		using PrivateFixedDriveTemporaryDirectory temporaryDirectory = new();
		string unsafeParentPath = System.IO.Path.Combine(
			temporaryDirectory.Path,
			"unsafe-parent");
		DirectoryInfo unsafeParent = Directory.CreateDirectory(unsafeParentPath);
		DirectorySecurity unsafeParentSecurity = unsafeParent.GetAccessControl(
			AccessControlSections.Access |
				AccessControlSections.Owner);
		unsafeParentSecurity.AddAccessRule(new FileSystemAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
			FileSystemRights.WriteAttributes,
			AccessControlType.Allow));
		unsafeParent.SetAccessControl(unsafeParentSecurity);
		string trustedRootPath = System.IO.Path.Combine(
			unsafeParentPath,
			"trusted-root");
		Assert.True(PrivateFixedDriveTemporaryDirectory
			.TryCreatePrivateDirectory(trustedRootPath));
		string executablePath = System.IO.Path.Combine(
			trustedRootPath,
			"codex.exe");
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);

		Assert.False(WindowsExecutablePathSecurity
			.IsPathAclSafeWithinTrustedRoot(
				executablePath,
				trustedRootPath));
	}

	[Fact]
	public void CodexOfficialPathResolver_CustomVisiblePathRequiresInstallerSignal()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string defaultVisibleBinDirectory = Path.Combine(
			temporaryDirectory.Path,
			"default-bin");
		string customVisibleBinDirectory = Path.Combine(
			temporaryDirectory.Path,
			"custom-bin");
		string unrelatedDirectory = Path.Combine(
			temporaryDirectory.Path,
			"unrelated");
		string customExecutablePath = Path.Combine(
			customVisibleBinDirectory,
			"codex.exe");

		Assert.True(
			CodexOfficialExecutablePathResolver.IsExpectedVisibleExecutablePath(
				customExecutablePath,
				defaultVisibleBinDirectory,
				customVisibleBinDirectory,
				pathValue: null));
		Assert.True(
			CodexOfficialExecutablePathResolver.IsExpectedVisibleExecutablePath(
				customExecutablePath,
				defaultVisibleBinDirectory,
				configuredInstallDirectory: null,
				$"{unrelatedDirectory}{Path.PathSeparator}\"{customVisibleBinDirectory}\""));
		Assert.False(
			CodexOfficialExecutablePathResolver.IsExpectedVisibleExecutablePath(
				customExecutablePath,
				defaultVisibleBinDirectory,
				configuredInstallDirectory: null,
				unrelatedDirectory));
	}

	[Fact]
	public async Task CodexOfficialPathResolver_WhenVisibleLinkBypassesCurrent_RejectsLayout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		OfficialJunctionLayout layout = await CreateOfficialJunctionLayoutAsync(
			temporaryDirectory,
			isPackageLayout: true);

		try
		{
			DeleteJunction(layout.VisibleBinPath);
			await CreateJunctionAsync(
				layout.VisibleBinPath,
				Path.GetDirectoryName(layout.PhysicalExecutablePath)!);

			OfficialCliExecutableValidationException exception = Assert.Throws<
				OfficialCliExecutableValidationException>(
					() => ResolveOfficialLayout(layout));

			Assert.Equal(
				OfficialCliExecutableValidationFailureReason.UnsafePath,
				exception.FailureReason);
			OfficialCliExecutableValidationException stagingException = Assert.Throws<
				OfficialCliExecutableValidationException>(
					() => ResolveOfficialStagingSourceLayout(layout));
			Assert.Equal(
				OfficialCliExecutableValidationFailureReason.UnsafePath,
				stagingException.FailureReason);
		}
		finally
		{
			DeleteJunction(layout.VisibleBinPath);
			DeleteJunction(layout.CurrentPath);
		}
	}

	[Fact]
	public async Task CodexOfficialPathResolver_WhenCurrentLeavesReleasesRoot_RejectsLayout()
	{
		using TemporaryDirectory temporaryDirectory = new();
		OfficialJunctionLayout layout = await CreateOfficialJunctionLayoutAsync(
			temporaryDirectory,
			isPackageLayout: true);

		try
		{
			string outsideReleasePath = Path.Combine(
				temporaryDirectory.Path,
				"outside",
				"0.144.1-x86_64-pc-windows-msvc");
			Directory.CreateDirectory(Path.Combine(outsideReleasePath, "bin"));
			File.WriteAllBytes(
				Path.Combine(outsideReleasePath, "bin", "codex.exe"),
				[0x4D, 0x5A]);
			DeleteJunction(layout.CurrentPath);
			await CreateJunctionAsync(layout.CurrentPath, outsideReleasePath);

			OfficialCliExecutableValidationException exception = Assert.Throws<
				OfficialCliExecutableValidationException>(
					() => ResolveOfficialLayout(layout));

			Assert.Equal(
				OfficialCliExecutableValidationFailureReason.UnsafePath,
				exception.FailureReason);
			OfficialCliExecutableValidationException stagingException = Assert.Throws<
				OfficialCliExecutableValidationException>(
					() => ResolveOfficialStagingSourceLayout(layout));
			Assert.Equal(
				OfficialCliExecutableValidationFailureReason.UnsafePath,
				stagingException.FailureReason);
		}
		finally
		{
			DeleteJunction(layout.VisibleBinPath);
			DeleteJunction(layout.CurrentPath);
		}
	}

	[Theory]
	[InlineData("CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\"", "Anthropic, PBC")]
	[InlineData("CN=\"OpenAI OpCo, LLC\", O=\"OpenAI OpCo, LLC\"", "OpenAI OpCo, LLC")]
	[InlineData("cn=X.AI LLC, O=X.AI LLC", "X.AI LLC")]
	public void HasExpectedSigner_WithExactCommonName_AcceptsSubject(
		string signerSubject,
		string expectedCommonName)
	{
		Assert.True(WindowsOfficialCliExecutableValidator.HasExpectedSigner(
			signerSubject,
			expectedCommonName));
	}

	[Theory]
	[InlineData("CN=Other, O=\"Acme\nCN=X.AI LLC\"", "X.AI LLC")]
	[InlineData("CN=\"\"\"X.AI LLC\"\"\"", "X.AI LLC")]
	[InlineData("CN=X.AI LLC+OU=Engineering, O=X.AI LLC", "X.AI LLC")]
	[InlineData("CN=OpenAI OpCo LLC, O=OpenAI", "OpenAI OpCo, LLC")]
	[InlineData("O=\"OpenAI OpCo, LLC\"", "OpenAI OpCo, LLC")]
	[InlineData("CN=\"unterminated", "Anthropic, PBC")]
	[InlineData(null, "Anthropic, PBC")]
	public void HasExpectedSigner_WithoutExactCommonName_RejectsSubject(
		string? signerSubject,
		string expectedCommonName)
	{
		Assert.False(WindowsOfficialCliExecutableValidator.HasExpectedSigner(
			signerSubject,
			expectedCommonName));
	}

	[Theory]
	[InlineData("codex-cli 0.144.1", 0, 144, 1)]
	[InlineData("codex-cli 1.0.0\r\n", 1, 0, 0)]
	public void CodexVersionParser_WithExpectedStableProduct_ReturnsVersion(
		string output,
		int major,
		int minor,
		int patch)
	{
		bool result = CodexCliVersionProbe.TryParseVersion(
			output,
			out Version version);

		Assert.True(result);
		Assert.Equal(new Version(major, minor, patch), version);
	}

	[Theory]
	[InlineData("")]
	[InlineData("0.144.1")]
	[InlineData("codex 0.144.1")]
	[InlineData("codex-cli 00.144.1")]
	[InlineData("codex-cli 0.144.1-alpha.1")]
	[InlineData("codex-cli 0.0.0")]
	[InlineData("codex-cli 0.144.1\nextra")]
	public void CodexVersionParser_WithUnknownFormat_FailsClosed(string output)
	{
		Assert.False(CodexCliVersionProbe.TryParseVersion(output, out _));
	}

	[Fact]
	public void CodexVersionProbeValidationBase_IsFixedDriveAndCurrentUserScoped()
	{
		string validationHomeBasePath =
			CodexCliVersionProbe.GetValidationHomeBasePath();
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
				"validation-v1"),
			validationHomeBasePath,
			ignoreCase: true);
		Assert.True(WindowsExecutablePathSecurity.IsFixedDrivePath(
			validationHomeBasePath));
	}

	[Fact]
	public void CodexVersionProbeValidationHome_IsProtectedWithinBase()
	{
		string validationHomeBasePath =
			CodexCliVersionProbe.GetValidationHomeBasePath();
		string validationHomeRoot =
			CodexCliVersionProbe.CreateValidationHomeRoot();

		try
		{
			Assert.True(WindowsExecutablePathSecurity
				.IsCanonicalNonReparseDirectory(validationHomeBasePath));
			Assert.True(WindowsExecutablePathSecurity
				.IsDirectoryPathAclSafeWithinTrustedRoot(
					validationHomeBasePath,
					validationHomeBasePath));
			Assert.Equal(
				Path.GetFullPath(validationHomeBasePath),
				Path.GetDirectoryName(Path.GetFullPath(validationHomeRoot)),
				ignoreCase: true);
			Assert.True(WindowsExecutablePathSecurity
				.IsCanonicalNonReparseDirectory(validationHomeRoot));
			Assert.True(WindowsExecutablePathSecurity
				.IsDirectoryPathAclSafeWithinTrustedRoot(
					validationHomeRoot,
					validationHomeBasePath));
		}
		finally
		{
			Assert.True(CodexCliVersionProbe.TryDeleteValidationHomeRoot(
				validationHomeRoot));
		}
	}

	[Fact]
	public void CodexVersionProbe_UsesIsolatedHomesAndCleansThem()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string fixtureDirectory = Path.Combine(
			temporaryDirectory.Path,
			"fixture");
		Directory.CreateDirectory(fixtureDirectory);
		string executablePath = CopyTerminalFixture(fixtureDirectory);
		File.WriteAllText(
			Path.Combine(fixtureDirectory, "codex-version-probe.mode"),
			string.Empty);
		string? ambientCodexHome =
			Environment.GetEnvironmentVariable("CODEX_HOME");
		string? ambientSqliteHome =
			Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
		string? ambientHome = Environment.GetEnvironmentVariable("HOME");
		string? ambientUserProfile =
			Environment.GetEnvironmentVariable("USERPROFILE");

		bool result = CodexCliVersionProbe.TryProbeVersion(
			executablePath,
			out Version detectedVersion);

		Assert.True(result);
		Assert.Equal(new Version(0, 144, 1), detectedVersion);
		string[] observedHomes = File.ReadAllLines(Path.Combine(
			fixtureDirectory,
			"codex-version-probe-homes.txt"));
		Assert.Equal(5, observedHomes.Length);
		Assert.NotEqual(observedHomes[0], observedHomes[1]);
		string validationHomeRoot =
			Path.GetDirectoryName(observedHomes[0]) ??
				throw new InvalidDataException(
					"The observed Codex validation home had no parent.");
		Assert.Equal(
			validationHomeRoot,
			Path.GetDirectoryName(observedHomes[1]));
		Assert.Equal(observedHomes[2], observedHomes[3]);
		Assert.Equal(observedHomes[2], observedHomes[4]);
		Assert.Equal(
			validationHomeRoot,
			Path.GetDirectoryName(observedHomes[2]));
		Assert.Equal(
			CodexCliVersionProbe.GetValidationHomeBasePath(),
			Path.GetDirectoryName(validationHomeRoot),
			ignoreCase: true);
		Assert.StartsWith(
			"probe-",
			Path.GetFileName(validationHomeRoot) ??
				throw new InvalidDataException(
					"The Codex validation home name was unavailable."),
			StringComparison.Ordinal);
		Assert.False(Directory.Exists(validationHomeRoot));
		Assert.Equal(
			ambientCodexHome,
			Environment.GetEnvironmentVariable("CODEX_HOME"));
		Assert.Equal(
			ambientSqliteHome,
			Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME"));
		Assert.Equal(
			ambientHome,
			Environment.GetEnvironmentVariable("HOME"));
		Assert.Equal(
			ambientUserProfile,
			Environment.GetEnvironmentVariable("USERPROFILE"));
	}

	[Fact]
	public async Task CodexVersionProbe_WhenRootSpawnsChild_ConfirmsTreeEmptyBeforeGateRelease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string fixtureDirectory = Path.Combine(
			temporaryDirectory.Path,
			"fixture");
		Directory.CreateDirectory(fixtureDirectory);
		string executablePath = CopyTerminalFixture(fixtureDirectory);
		File.WriteAllText(
			Path.Combine(fixtureDirectory, "codex-version-probe.mode"),
			"spawn-child-then-exit");
		CodexAccountOperationGate operationGate = new();
		ProviderProcessOperationTracker operationTracker = new();
		Guid accountId = Guid.NewGuid();
		IDisposable rawLease = await operationGate.EnterAsync(
			accountId,
			CancellationToken.None);
		IDisposable operationLease = operationTracker.HoldLease(rawLease);

		try
		{
			bool result = await ProviderProcessExecution.RunSynchronousAsync(
				() => CodexCliVersionProbe.HasExpectedVersion(executablePath),
				CancellationToken.None,
				operationTracker);

			Assert.True(result);
			string[] processIds = File.ReadAllText(Path.Combine(
				fixtureDirectory,
				"runner-pids.txt")).Split(';');
			Assert.Equal(2, processIds.Length);
			AssertProcessIsNotRunning(int.Parse(processIds[0]));
			AssertProcessIsNotRunning(int.Parse(processIds[1]));
			string[] observedHomes = File.ReadAllLines(Path.Combine(
				fixtureDirectory,
				"codex-version-probe-homes.txt"));
			string validationHomeRoot =
				Path.GetDirectoryName(observedHomes[0]) ??
				throw new InvalidDataException(
					"The observed Codex validation home had no parent.");
			Assert.False(Directory.Exists(validationHomeRoot));

			using CancellationTokenSource blockedSource = new(
				TimeSpan.FromMilliseconds(200));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				operationGate.EnterAsync(accountId, blockedSource.Token).AsTask());
		}
		finally
		{
			operationLease.Dispose();
		}

		using CancellationTokenSource reacquireSource = new(
			TimeSpan.FromSeconds(2));
		using IDisposable reacquiredLease = await operationGate.EnterAsync(
			accountId,
			reacquireSource.Token);
	}

	[Fact]
	public void ContainedProcess_WhenLaunchIsBlockedBeforeResume_ConfirmsRootExit()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string fixtureDirectory = Path.Combine(
			temporaryDirectory.Path,
			"fixture");
		Directory.CreateDirectory(fixtureDirectory);
		string executablePath = CopyTerminalFixture(fixtureDirectory);
		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			WorkingDirectory = fixtureDirectory
		};
		startInfo.ArgumentList.Add("--version");
		int boundaryCheckCount = 0;

		WindowsJobContainedProcessLaunchException exception = Assert.Throws<
			WindowsJobContainedProcessLaunchException>(() =>
				WindowsJobContainedProcess.Start(
					startInfo,
					() =>
					{
						if (Interlocked.Increment(
							ref boundaryCheckCount) == 2)
						{
							throw new CodexCliVersionProbeContainmentException(
								"Containment was compromised before resume.");
						}
					}));

		Assert.Equal(2, boundaryCheckCount);
		Assert.True(exception.IsLaunchBlocked);
		Assert.True(exception.IsTreeEmptyConfirmed);
		Assert.IsType<CodexCliVersionProbeContainmentException>(
			exception.InnerException);
		CodexVersionProbeContainmentState containmentState = new();

		CodexCliVersionProbeException classifiedFailure =
			CodexCliVersionProbe.CreateLaunchFailure(
				exception,
				containmentState);

		Assert.IsType<CodexCliVersionProbeContainmentException>(
			classifiedFailure);
		Assert.Same(exception, classifiedFailure.InnerException);
		Assert.True(containmentState.IsCompromised);
	}

	[Fact]
	public async Task ContainedProcess_WhenOwnerHandleCloses_KillsProcessTree()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string fixtureDirectory = Path.Combine(
			temporaryDirectory.Path,
			"fixture");
		Directory.CreateDirectory(fixtureDirectory);
		string executablePath = CopyTerminalFixture(fixtureDirectory);
		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = fixtureDirectory
		};
		startInfo.ArgumentList.Add("spawn-child-and-hang");
		WindowsJobContainedProcess? containedProcess = null;
		int[] processIds = [];

		try
		{
			containedProcess = WindowsJobContainedProcess.Start(
				startInfo,
				static () => { });
			using StreamReader outputReader = new(
				containedProcess.StandardOutput,
				leaveOpen: true);
			string announcement = await outputReader.ReadLineAsync()
				.WaitAsync(TimeSpan.FromSeconds(5)) ??
				throw new InvalidDataException(
					"The contained fixture did not report its process tree.");
			processIds = announcement
				.Split(';')
				.Select(value => int.Parse(value.Split(':')[1]))
				.ToArray();
			Assert.Equal(2, processIds.Length);
			Assert.All(processIds, processId =>
				Assert.True(IsProcessRunning(processId)));

			containedProcess.Dispose();
			containedProcess = null;

			await WaitForProcessesToExitAsync(
				processIds,
				TimeSpan.FromSeconds(5));
			Assert.All(processIds, AssertProcessIsNotRunning);
		}
		finally
		{
			containedProcess?.Dispose();
			foreach (int processId in processIds)
			{
				TryTerminateFixtureProcess(processId);
			}
		}
	}

	[Fact]
	public async Task CodexVersionProbeContainmentState_AfterCompromise_BlocksNewLaunches()
	{
		CodexVersionProbeContainmentState containmentState = new();
		IDisposable transition = containmentState.EnterLaunchTransition();
		Task compromiseTask = Task.Run(containmentState.MarkCompromised);

		try
		{
			Assert.True(SpinWait.SpinUntil(
				() => containmentState.IsCompromised,
				TimeSpan.FromSeconds(2)));
			Assert.Throws<CodexCliVersionProbeContainmentException>(
				containmentState.ThrowIfCompromised);
			Assert.False(compromiseTask.IsCompleted);
		}
		finally
		{
			transition.Dispose();
		}

		await compromiseTask.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Throws<CodexCliVersionProbeContainmentException>(
			containmentState.ThrowIfCompromised);
		Assert.Throws<CodexCliVersionProbeContainmentException>(() =>
			containmentState.EnterLaunchTransition());
	}

	[Fact]
	public void ValidateStaged_WhenCodexProbeContainmentFails_QuarantinesProtectedLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		CodexVersionProbeContainmentState containmentState = new();
		WindowsOfficialCliExecutableLease? quarantinedLease = null;
		WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			_ =>
			{
				containmentState.MarkCompromised();
				throw new CodexCliVersionProbeContainmentException(
					"Injected containment failure.");
			},
			_ => CreateSignature(0, OpenAiSignerSubject),
			_ => true,
			_ => true,
			_ => true,
			lease =>
			{
				quarantinedLease = lease;
				return containmentState.RetainExecutableLease(lease);
			});

		try
		{
			OfficialCliExecutableValidationException exception = Assert.Throws<
				OfficialCliExecutableValidationException>(
					() => validator.ValidateStaged(executableLease));
			executableLease.Dispose();

			Assert.Equal(
				OfficialCliExecutableValidationFailureReason
					.VersionProbeContainmentFailed,
				exception.FailureReason);
			Assert.Equal(1, containmentState.QuarantinedExecutableLeaseCount);
			Assert.NotNull(quarantinedLease);
			Assert.Throws<IOException>(() =>
			{
				using FileStream writer = new(
					executablePath,
					FileMode.Open,
					FileAccess.Write,
					FileShare.None);
			});
		}
		finally
		{
			quarantinedLease?.Dispose();
			executableLease.Dispose();
		}
	}

	[Fact]
	public void ValidateStaged_AfterCodexProbeCompromise_DoesNotQuarantineAnotherLease()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(
			temporaryDirectory,
			"codex.exe");
		CodexVersionProbeContainmentState containmentState = new();
		containmentState.MarkCompromised();
		WindowsOfficialCliExecutableLease? rejectedLease = null;
		using WindowsOfficialCliExecutableLease executableLease =
			WindowsOfficialCliExecutableLease.CreateProtected(
				executablePath,
				new FileStream(
					executablePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read));
		WindowsOfficialCliExecutableValidator validator = new(
			"OpenAI OpCo, LLC",
			_ => true,
			_ => CreateSignature(0, OpenAiSignerSubject),
			_ => true,
			_ => true,
			_ => true,
			lease =>
			{
				rejectedLease = lease;
				return containmentState.RetainExecutableLease(lease);
			});

		OfficialCliExecutableValidationException exception = Assert.Throws<
			OfficialCliExecutableValidationException>(
				() => validator.ValidateStaged(executableLease));

		Assert.Equal(
			OfficialCliExecutableValidationFailureReason
				.VersionProbeContainmentFailed,
			exception.FailureReason);
		Assert.Equal(0, containmentState.QuarantinedExecutableLeaseCount);
		Assert.NotNull(rejectedLease);
		Assert.False(rejectedLease.IsProtected);
		Assert.True(executableLease.IsProtected);
	}

	[Fact]
	public void CodexResolver_AfterProbeCompromise_DoesNotInvokeStager()
	{
		CodexVersionProbeContainmentState containmentState = new();
		containmentState.MarkCompromised();
		CountingCodexExecutableStager stager = new();
		WindowsOfficialCliExecutableValidator validator = CreateValidator(
			"OpenAI OpCo, LLC",
			OpenAiSignerSubject);

		Assert.Throws<CodexCliContainmentException>(() =>
			CodexAppServerUsagePoller.ResolveExecutablePath(
				["C:\\missing-codex.exe"],
				validator,
				stager,
				containmentState));

		Assert.Equal(0, stager.StageCallCount);
		Assert.Equal(0, containmentState.QuarantinedExecutableLeaseCount);
	}

	[Fact]
	public void ProcessTransport_AfterVersionProbeCompromise_BlocksBeforeProcessStart()
	{
		CodexVersionProbeContainmentState containmentState = new();
		containmentState.MarkCompromised();
		ProviderProcessOperationTracker operationTracker = new();
		ProcessStartInfo startInfo = new("C:\\missing-codex.exe")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		Assert.Throws<CodexCliVersionProbeContainmentException>(() =>
			new CodexAppServerUsagePoller.ProcessTransport(
				startInfo,
				operationTracker,
				_ => Task.CompletedTask,
				containmentState: containmentState));
	}

	[Fact]
	public async Task CodexVersionProbeCleanup_WithReparseDescendant_DoesNotTraverseTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string externalTargetPath = Path.Combine(
			temporaryDirectory.Path,
			"external-target");
		Directory.CreateDirectory(externalTargetPath);
		string sentinelPath = Path.Combine(externalTargetPath, "sentinel.txt");
		File.WriteAllText(sentinelPath, "preserve");
		string validationHomeRoot =
			CodexCliVersionProbe.CreateValidationHomeRoot();
		string junctionPath = Path.Combine(validationHomeRoot, "linked-state");

		try
		{
			await CreateJunctionAsync(junctionPath, externalTargetPath);

			bool result = CodexCliVersionProbe.TryDeleteValidationHomeRoot(
				validationHomeRoot);

			Assert.False(result);
			Assert.True(Directory.Exists(validationHomeRoot));
			Assert.Equal("preserve", File.ReadAllText(sentinelPath));
		}
		finally
		{
			DeleteJunction(junctionPath);
			Assert.True(CodexCliVersionProbe.TryDeleteValidationHomeRoot(
				validationHomeRoot));
		}
	}

	[Fact]
	public void CodexVersionProbeCleanup_WithNonGuidSibling_DoesNotDeleteIt()
	{
		string validationHomeBasePath =
			CodexCliVersionProbe.GetValidationHomeBasePath();
		string preparedValidationHomeRoot =
			CodexCliVersionProbe.CreateValidationHomeRoot();
		Assert.True(CodexCliVersionProbe.TryDeleteValidationHomeRoot(
			preparedValidationHomeRoot));
		string unrelatedPath = Path.Combine(
			validationHomeBasePath,
			$"probe-not-owned-{Guid.NewGuid():N}");
		Directory.CreateDirectory(unrelatedPath);
		string sentinelPath = Path.Combine(unrelatedPath, "sentinel.txt");
		File.WriteAllText(sentinelPath, "preserve");

		try
		{
			Assert.False(CodexCliVersionProbe.TryDeleteValidationHomeRoot(
				unrelatedPath));
			Assert.Equal("preserve", File.ReadAllText(sentinelPath));
		}
		finally
		{
			if (Directory.Exists(unrelatedPath))
			{
				Directory.Delete(unrelatedPath, recursive: true);
			}
		}
	}

	private static WindowsOfficialCliExecutableValidator CreateValidator(
		string expectedSignerCommonName,
		string signerSubject,
		int trustStatus = 0,
		bool hasExpectedVersion = true)
	{
		return new WindowsOfficialCliExecutableValidator(
			expectedSignerCommonName,
			_ => hasExpectedVersion,
			_ => CreateSignature(trustStatus, signerSubject),
			_ => true,
			_ => true,
			_ => true);
	}

	private static async Task<OfficialJunctionLayout>
		CreateOfficialJunctionLayoutAsync(
			TemporaryDirectory temporaryDirectory,
			bool isPackageLayout)
	{
		return await CreateOfficialJunctionLayoutAsync(
			temporaryDirectory.Path,
			isPackageLayout);
	}

	private static async Task<OfficialJunctionLayout>
		CreateOfficialJunctionLayoutAsync(
			string rootPath,
			bool isPackageLayout)
	{
		string standaloneRoot = Path.Combine(
			rootPath,
			".codex",
			"packages",
			"standalone");
		string releasePath = Path.Combine(
			standaloneRoot,
			"releases",
			"0.144.1-x86_64-pc-windows-msvc");
		string physicalExecutableDirectory = isPackageLayout
			? Path.Combine(releasePath, "bin")
			: releasePath;
		string physicalExecutablePath = Path.Combine(
			physicalExecutableDirectory,
			"codex.exe");
		Directory.CreateDirectory(physicalExecutableDirectory);
		File.WriteAllBytes(physicalExecutablePath, [0x4D, 0x5A]);
		string currentPath = Path.Combine(standaloneRoot, "current");
		await CreateJunctionAsync(currentPath, releasePath);
		string visibleRootPath = Path.Combine(
			rootPath,
			"custom-codex-install");
		Directory.CreateDirectory(visibleRootPath);
		string visibleBinPath = Path.Combine(
			visibleRootPath,
			"custom-visible-bin");
		await CreateJunctionAsync(
			visibleBinPath,
			isPackageLayout ? Path.Combine(currentPath, "bin") : currentPath);
		string visibleExecutablePath = Path.Combine(
			visibleBinPath,
			"codex.exe");
		return new OfficialJunctionLayout(
			Path.Combine(rootPath, ".codex"),
			currentPath,
			physicalExecutablePath,
			standaloneRoot,
			visibleBinPath,
			visibleExecutablePath);
	}

	private static WindowsAuthenticodeInspection CreateSignature(
		int trustStatus,
		string signerSubject)
	{
		return new WindowsAuthenticodeInspection(
			trustStatus,
			signerSubject,
			new string('A', 40));
	}

	private static async Task CreateJunctionAsync(
		string junctionPath,
		string targetPath)
	{
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
		startInfo.ArgumentList.Add(junctionPath);
		startInfo.ArgumentList.Add(targetPath);
		using Process process = Process.Start(startInfo) ??
			throw new InvalidOperationException(
				"The junction creation process did not start.");
		Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
		Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		string standardOutput = await standardOutputTask;
		string standardError = await standardErrorTask;

		Assert.True(
			process.ExitCode == 0,
			$"mklink failed: {standardOutput} {standardError}");
	}

	private static void DeleteJunction(string junctionPath)
	{
		if (Directory.Exists(junctionPath))
		{
			Directory.Delete(junctionPath, recursive: false);
		}
	}

	private static bool IsProcessRunning(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	private static async Task WaitForProcessesToExitAsync(
		IReadOnlyCollection<int> processIds,
		TimeSpan timeout)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
		while (processIds.Any(IsProcessRunning))
		{
			if (DateTimeOffset.UtcNow >= deadline)
			{
				throw new TimeoutException(
					"The contained fixture process tree did not exit in time.");
			}

			await Task.Delay(TimeSpan.FromMilliseconds(20));
		}
	}

	private static void TryTerminateFixtureProcess(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit(2000);
			}
		}
		catch (ArgumentException)
		{
		}
	}

	private static void AssertProcessIsNotRunning(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			Assert.True(
				process.HasExited,
				$"Expected process {processId} to have exited.");
		}
		catch (ArgumentException)
		{
			// A missing process is the normal confirmed-quiescence result.
		}
	}

	private static string CreateExecutable(
		TemporaryDirectory temporaryDirectory,
		string fileName)
	{
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			fileName);
		File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
		return executablePath;
	}

	private static string CopyTerminalFixture(string destinationDirectory)
	{
		Assembly fixtureAssembly = Assembly.Load(
			new AssemblyName("AiUsageDashboard.TerminalFixture"));
		string sourceDirectory = Path.GetDirectoryName(fixtureAssembly.Location) ??
			throw new InvalidOperationException(
				"The terminal fixture output directory is unavailable.");
		string sourceExecutablePath = Path.ChangeExtension(
			fixtureAssembly.Location,
			".exe");

		foreach (string sourcePath in Directory.EnumerateFiles(
			sourceDirectory,
			"AiUsageDashboard.TerminalFixture.*",
			SearchOption.TopDirectoryOnly))
		{
			File.Copy(
				sourcePath,
				Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)),
				overwrite: true);
		}

		string executablePath = Path.Combine(
			destinationDirectory,
			"AiUsageDashboard.TerminalFixture.exe");
		File.Copy(sourceExecutablePath, executablePath, overwrite: true);
		return executablePath;
	}

	private static string ResolveOfficialLayout(OfficialJunctionLayout layout)
	{
		return CodexOfficialExecutablePathResolver.Resolve(
			layout.VisibleExecutablePath,
			layout.StandaloneRoot,
			"x86_64-pc-windows-msvc",
			_ => true,
			_ => true,
			_ => true,
			_ => true);
	}

	private static string ResolveOfficialStagingSourceLayout(
		OfficialJunctionLayout layout)
	{
		return CodexOfficialExecutablePathResolver.ResolveStagingSource(
			layout.VisibleExecutablePath,
			layout.StandaloneRoot,
			"x86_64-pc-windows-msvc",
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsCanonicalNonReparseDirectory);
	}
}
