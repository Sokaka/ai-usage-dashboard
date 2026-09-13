using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class CopilotCliExecutableResolverTests
{
	[Theory]
	[InlineData("1.0.79", true)]
	[InlineData("1.0.80", true)]
	[InlineData("1.9.999", true)]
	[InlineData("1.0.78", false)]
	[InlineData("0.0.0", false)]
	[InlineData("2.0.0", false)]
	[InlineData("1.0", false)]
	[InlineData("1.0.79.0", false)]
	[InlineData("01.0.79", false)]
	[InlineData("1.0.079", false)]
	[InlineData("1.0.79-preview", false)]
	[InlineData("1.0.79+build", false)]
	[InlineData(" 1.0.79", false)]
	[InlineData("1.0.79 ", false)]
	[InlineData("v1.0.79", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void HasSupportedVersion_RequiresCanonicalStableVersion(string? version, bool expected)
	{
		Assert.Equal(expected, CopilotCliExecutableResolver.HasSupportedVersion(
			"GitHub Copilot CLI", version, version));
	}

	[Theory]
	[InlineData("GitHub CLI", "1.0.79", "1.0.79")]
	[InlineData("GitHub Copilot CLI", "1.0.79", "1.0.80")]
	[InlineData("GitHub Copilot CLI", "1.0.79", null)]
	[InlineData(null, "1.0.79", "1.0.79")]
	public void HasSupportedVersion_RejectsDifferentProductOrConflictingMetadata(
		string? productName, string? fileVersion, string? productVersion)
	{
		Assert.False(CopilotCliExecutableResolver.HasSupportedVersion(
			productName, fileVersion, productVersion));
	}

	[Fact]
	public void Resolve_WithoutCandidates_DoesNotStageOrReadVersion()
	{
		Assert.Null(CopilotCliExecutableResolver.Resolve(
			[],
			_ => throw new InvalidOperationException("No staging expected."),
			_ => throw new InvalidOperationException("No version read expected.")));
	}

	[Fact]
	public void Resolve_ReadsOnlyLockedStagedCopy_AndTransfersLeaseToCaller()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(temporaryDirectory.Path, "copilot.exe");
		string staged = CreateFile(temporaryDirectory.Path, "staged.exe");
		WindowsOfficialCliExecutableLease? stagedLease = null;
		using WindowsOfficialCliExecutableLease? result = CopilotCliExecutableResolver.Resolve(
			[source],
			path =>
			{
				Assert.Equal(source, path);
				stagedLease = CreateLease(staged);
				return stagedLease;
			},
			path =>
			{
				Assert.Equal(staged, path);
				Assert.True(stagedLease!.IsProtected);
				Assert.Throws<IOException>(() =>
				{
					using FileStream write = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
				});
				return ("GitHub Copilot CLI", "1.0.79", "1.0.79");
			});

		Assert.Same(stagedLease, result);
		Assert.True(result!.IsProtected);
	}

	[Fact]
	public void Resolve_RejectsInvalidVersion_DisposesLeaseAndTriesNextCandidate()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string first = CreateFile(temporaryDirectory.Path, "first.exe");
		string second = CreateFile(temporaryDirectory.Path, "second.exe");
		List<WindowsOfficialCliExecutableLease> leases = new();
		using WindowsOfficialCliExecutableLease? result = CopilotCliExecutableResolver.Resolve(
			[first, second],
			path =>
			{
				WindowsOfficialCliExecutableLease lease = CreateLease(path);
				leases.Add(lease);
				return lease;
			},
			path => path == first
				? ("GitHub Copilot CLI", "2.0.0", "2.0.0")
				: ("GitHub Copilot CLI", "1.1.0", "1.1.0"));

		Assert.Equal(second, result!.ExecutablePath);
		Assert.False(leases[0].IsProtected);
		Assert.True(leases[1].IsProtected);
	}

	[Fact]
	public void Resolve_RejectsUnprotectedLease_BeforeVersionRead()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(temporaryDirectory.Path, "copilot.exe");
		Assert.Throws<IOException>(() => CopilotCliExecutableResolver.Resolve(
			[source],
			WindowsOfficialCliExecutableLease.CreateUnprotected,
			_ => throw new InvalidOperationException("No version read expected.")));
	}

	[Fact]
	public void Resolve_WhenStagingFails_PreservesOriginalCause()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(temporaryDirectory.Path, "copilot.exe");
		WindowsOfficialCliExecutableStagingException failure = new(
			WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
			"Synthetic wrong signer.");
		IOException exception = Assert.Throws<IOException>(() => CopilotCliExecutableResolver.Resolve(
			[source],
			_ => throw failure,
			_ => throw new InvalidOperationException("No version read expected.")));

		AggregateException aggregate = Assert.IsType<AggregateException>(exception.InnerException);
		Assert.Same(failure, Assert.Single(aggregate.InnerExceptions).InnerException);
		Assert.Contains(source, aggregate.InnerExceptions[0].Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Resolve_WhenVersionReadFails_ReleasesLockAndPreservesCause()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(temporaryDirectory.Path, "copilot.exe");
		IOException failure = new("Synthetic metadata read failure.");
		WindowsOfficialCliExecutableLease? lease = null;
		IOException exception = Assert.Throws<IOException>(() => CopilotCliExecutableResolver.Resolve(
			[source],
			path => lease = CreateLease(path),
			_ => throw failure));

		Assert.False(lease!.IsProtected);
		AggregateException aggregate = Assert.IsType<AggregateException>(exception.InnerException);
		Assert.Same(failure, Assert.Single(aggregate.InnerExceptions).InnerException);
		using FileStream write = new(source, FileMode.Open, FileAccess.Write, FileShare.None);
	}

	[Theory]
	[InlineData("copilot.cmd")]
	[InlineData("copilot.ps1")]
	public void Resolve_RejectsWrappersWithoutStaging(string fileName)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(temporaryDirectory.Path, fileName);
		Assert.Throws<IOException>(() => CopilotCliExecutableResolver.Resolve(
			[source],
			_ => throw new InvalidOperationException("No wrapper staging expected."),
			_ => throw new InvalidOperationException("No version read expected.")));
	}

	[Fact]
	public void FindCandidates_IncludesWingetNpmAndPathNativeExecutablesOnly()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string local = Path.Combine(temporaryDirectory.Path, "local");
		string programs = Path.Combine(temporaryDirectory.Path, "programs");
		string roaming = Path.Combine(temporaryDirectory.Path, "roaming");
		string prefix = Path.Combine(temporaryDirectory.Path, "custom-npm");
		string pathDirectory = Path.Combine(temporaryDirectory.Path, "path");
		string userWinget = CreateFile(Path.Combine(local, "Microsoft", "WinGet", "Packages",
			"GitHub.Copilot_Microsoft.Winget.Source_test"), "copilot.exe");
		string machineWinget = CreateFile(Path.Combine(programs, "WinGet", "Packages",
			"GitHub.Copilot_Microsoft.Winget.Source_test"), "copilot.exe");
		string npmHoisted = CreateFile(Path.Combine(roaming, "npm", "node_modules", "@github",
			"copilot-win32-x64"), "copilot.exe");
		string npmNested = CreateFile(Path.Combine(prefix, "node_modules", "@github", "copilot",
			"node_modules", "@github", "copilot-win32-x64"), "copilot.exe");
		string onPath = CreateFile(pathDirectory, "copilot.exe");
		CreateFile(pathDirectory, "copilot.cmd");
		CreateFile(pathDirectory, "copilot.ps1");
		CreateFile(Path.Combine(local, "Microsoft", "WinGet", "Packages", "Other.Package"), "copilot.exe");
		CreateFile(Path.Combine(local, "runtimes", "win-x64", "native"), "copilot.exe");

		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			local, programs, roaming, string.Join(Path.PathSeparator, prefix, pathDirectory, pathDirectory));

		Assert.Equal([userWinget, machineWinget, npmHoisted, npmNested, onPath], search.Candidates);
		Assert.Empty(search.Failures);
	}

	[Fact]
	public void FindCandidates_WithoutNativeExecutable_ReturnsEmpty()
	{
		using TemporaryDirectory temporaryDirectory = new();
		CreateFile(temporaryDirectory.Path, "copilot.cmd");
		CreateFile(temporaryDirectory.Path, "copilot.ps1");
		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			null, null, null, temporaryDirectory.Path);
		Assert.Empty(search.Candidates);
		Assert.Empty(search.Failures);
	}

	[Fact]
	public void FindCandidates_WhenWingetEnumerationExceedsLimit_ReportsRoot()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string root = Path.Combine(temporaryDirectory.Path, "WinGet", "Packages");
		for (int index = 0; index <= CopilotCliExecutableResolver.MaximumWingetPackageDirectories; index++)
		{
			Directory.CreateDirectory(Path.Combine(root, $"GitHub.Copilot_Microsoft.Winget.Source_{index}"));
		}

		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			null, temporaryDirectory.Path, null, null);
		Assert.Empty(search.Candidates);
		IOException exception = Assert.IsType<IOException>(Assert.Single(search.Failures));
		Assert.Contains(root, exception.Message, StringComparison.Ordinal);
		Assert.IsType<IOException>(exception.InnerException);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Resolve_WhenLaterPathProbeFails_StillUsesDiscoveredWingetOrNpmCli(bool useWinget)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourceDirectory = useWinget
			? Path.Combine(temporaryDirectory.Path, "Microsoft", "WinGet", "Packages",
				"GitHub.Copilot_Microsoft.Winget.Source_test")
			: Path.Combine(temporaryDirectory.Path, "npm", "node_modules", "@github", "copilot-win32-x64");
		string source = CreateFile(sourceDirectory, "copilot.exe");
		string inaccessible = Path.Combine(temporaryDirectory.Path, "inaccessible");
		UnauthorizedAccessException cause = new("Synthetic inaccessible search directory.");
		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			useWinget ? temporaryDirectory.Path : null,
			null,
			useWinget ? null : temporaryDirectory.Path,
			inaccessible,
			readAttributes: path => path.StartsWith(inaccessible, StringComparison.OrdinalIgnoreCase)
				? throw cause
				: File.GetAttributes(path));

		Assert.Equal(source, Assert.Single(search.Candidates));
		Assert.NotEmpty(search.Failures);
		Assert.All(search.Failures, failure => Assert.Same(cause, failure.InnerException));
		using WindowsOfficialCliExecutableLease? lease = CopilotCliExecutableResolver.Resolve(
			search, CreateLease, _ => ("GitHub Copilot CLI", "1.0.79", "1.0.79"));
		Assert.Equal(source, lease!.ExecutablePath);
	}

	[Fact]
	public void Resolve_WhenLaterPathIsInvalid_StillUsesDiscoveredNativeCli()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(Path.Combine(temporaryDirectory.Path, "npm", "node_modules", "@github",
			"copilot-win32-x64"), "copilot.exe");
		string invalidPath = Path.Combine(temporaryDirectory.Path, "invalid") + char.MinValue;
		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			null, null, temporaryDirectory.Path, invalidPath);

		Assert.Equal(source, Assert.Single(search.Candidates));
		Assert.NotEmpty(search.Failures);
		Assert.All(search.Failures, failure => Assert.IsType<ArgumentException>(failure.InnerException));
		using WindowsOfficialCliExecutableLease? lease = CopilotCliExecutableResolver.Resolve(
			search, CreateLease, _ => ("GitHub Copilot CLI", "1.0.79", "1.0.79"));
		Assert.Equal(source, lease!.ExecutablePath);
	}

	[Fact]
	public void Resolve_WhenSearchAndCandidateBothFail_PreservesBothCauses()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(temporaryDirectory.Path, "copilot.exe");
		IOException discoveryFailure = new("Synthetic failed search location.");
		WindowsOfficialCliExecutableStagingException stagingFailure = new(
			WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature,
			"Synthetic invalid signature.");
		CopilotCliExecutableResolver.CandidateSearchResult search = new([source], [discoveryFailure]);

		IOException exception = Assert.Throws<IOException>(() => CopilotCliExecutableResolver.Resolve(
			search,
			_ => throw stagingFailure,
			_ => throw new InvalidOperationException("No version read expected.")));

		AggregateException aggregate = Assert.IsType<AggregateException>(exception.InnerException);
		Assert.Equal(2, aggregate.InnerExceptions.Count);
		Assert.Same(discoveryFailure, aggregate.InnerExceptions[0]);
		Assert.Same(stagingFailure, aggregate.InnerExceptions[1].InnerException);
	}

	[Fact]
	public void Resolve_WhenOnlySearchLocationIsInaccessible_ReportsOriginalCause()
	{
		using TemporaryDirectory temporaryDirectory = new();
		UnauthorizedAccessException cause = new("Synthetic denied path.");
		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			null, null, null, temporaryDirectory.Path,
			readAttributes: _ => throw cause);

		IOException exception = Assert.Throws<IOException>(() => CopilotCliExecutableResolver.Resolve(
			search,
			_ => throw new InvalidOperationException("No staging expected."),
			_ => throw new InvalidOperationException("No version read expected.")));

		AggregateException aggregate = Assert.IsType<AggregateException>(exception.InnerException);
		Assert.NotEmpty(aggregate.InnerExceptions);
		Assert.All(aggregate.InnerExceptions, failure => Assert.Same(cause, failure.InnerException));
		Assert.Contains(temporaryDirectory.Path, aggregate.InnerExceptions[0].Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("//server.example.test/share/copilot")]
	[InlineData("//?/UNC/server.example.test/share/copilot")]
	[InlineData("//?/C:/copilot")]
	public void FindCandidates_RejectsNetworkAndDevicePathsBeforeDriveOrMetadataProbe(string unsafePath)
	{
		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			unsafePath, unsafePath, unsafePath, unsafePath,
			isFixedDrivePath: _ => throw new InvalidOperationException("No drive probe expected."),
			readAttributes: _ => throw new InvalidOperationException("No metadata probe expected."));

		Assert.Empty(search.Candidates);
		Assert.NotEmpty(search.Failures);
		Assert.All(search.Failures, failure => Assert.IsType<IOException>(failure.InnerException));
	}

	[Fact]
	public void FindCandidates_RejectsNonFixedDriveBeforeMetadataProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		CopilotCliExecutableResolver.CandidateSearchResult search = CopilotCliExecutableResolver.FindCandidates(
			temporaryDirectory.Path, temporaryDirectory.Path, temporaryDirectory.Path, temporaryDirectory.Path,
			isFixedDrivePath: _ => false,
			readAttributes: _ => throw new InvalidOperationException("No metadata probe expected."));

		Assert.Empty(search.Candidates);
		Assert.NotEmpty(search.Failures);
	}

	[Fact]
	public void FindCandidates_WhenPathExceedsLimit_FailsClosed()
	{
		string path = new(Path.PathSeparator, CopilotCliExecutableResolver.MaximumPathDirectories);
		Assert.Throws<IOException>(() => CopilotCliExecutableResolver.FindCandidates(null, null, null, path));
	}

	[Theory]
	[InlineData("CN=\"GitHub, Inc.\", O=\"GitHub, Inc.\"", 0, true)]
	[InlineData("CN=GitHub-Impostor, O=\"GitHub, Inc.\"", 0, false)]
	[InlineData("CN=\"GitHub, Inc.\", O=\"GitHub, Inc.\"", -1, false)]
	public void Stage_RequiresTrustedGitHubSignature(string subject, int trustStatus, bool accepted)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string source = CreateFile(temporaryDirectory.Path, "copilot.exe");
		string trustedRoot = Path.Combine(temporaryDirectory.Path, "trusted");
		using CopilotCliExecutableStager stager = new(
			() => trustedRoot,
			_ => new WindowsAuthenticodeInspection(trustStatus, subject, new string('A', 40)),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			_ => true,
			(_, _) => true,
			(_, _) => true,
			path =>
			{
				Directory.CreateDirectory(path);
				return true;
			},
			File.Exists);

		if (accepted)
		{
			using WindowsOfficialCliExecutableLease lease = stager.Stage(source);
			Assert.True(lease.IsProtected);
			Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(lease.ExecutablePath));
			Assert.StartsWith("copilot-", Path.GetFileName(lease.ExecutablePath), StringComparison.Ordinal);
		}
		else
		{
			WindowsOfficialCliExecutableStagingException exception =
				Assert.Throws<WindowsOfficialCliExecutableStagingException>(() => stager.Stage(source));
			Assert.Equal(WindowsOfficialCliExecutableStagingFailureReason.InvalidSignature, exception.FailureReason);
		}
	}

	private static string CreateFile(string directory, string fileName)
	{
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, fileName);
		File.WriteAllBytes(path, [0x4D, 0x5A, 0x01, 0x02]);
		return path;
	}

	private static WindowsOfficialCliExecutableLease CreateLease(string path)
	{
		return WindowsOfficialCliExecutableLease.CreateProtected(
			path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
	}
}
