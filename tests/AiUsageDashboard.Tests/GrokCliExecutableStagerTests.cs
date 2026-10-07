using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class GrokCliExecutableStagerTests
{
	private const string ExactSignerSubject =
		"CN=X.AI LLC, O=X.AI LLC, L=Palo Alto, S=California, C=US";

	[Fact]
	public void Stage_WithTrustedSource_ReturnsProtectedLeaseForSamePath()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		List<string> inspectedPaths = new();
		using GrokCliExecutableStager stager = CreateStager(path =>
		{
			inspectedPaths.Add(path);
			return CreateTrustedSignature();
		});

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);

		Assert.True(lease.IsProtected);
		Assert.Equal(Path.GetFullPath(sourcePath), lease.ExecutablePath);
		Assert.Equal([Path.GetFullPath(sourcePath)], inspectedPaths);
		Assert.Equal([Path.GetFullPath(sourcePath)], Directory.EnumerateFiles(
			temporaryDirectory.Path));
	}

	[Fact]
	public void Stage_EachAcquisition_RevalidatesSignature()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		int inspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(_ =>
		{
			inspectionCount++;
			return CreateTrustedSignature();
		});

		using (WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath))
		{
		}

		using (WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath))
		{
		}

		Assert.Equal(2, inspectionCount);
	}

	[Fact]
	public void Stage_PerformsSignatureInspectionWhileSourceIsLocked()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		using GrokCliExecutableStager stager = CreateStager(path =>
		{
			Assert.Equal(Path.GetFullPath(sourcePath), path);
			AssertWriteAndDeleteBlocked(path);
			return CreateTrustedSignature();
		});

		using WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);

		Assert.True(lease.IsProtected);
	}

	[Fact]
	public void ProtectedLease_BlocksWriteAndDeleteUntilLastDuplicateIsDisposed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		using GrokCliExecutableStager stager = CreateStager(
			_ => CreateTrustedSignature());
		WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath);
		WindowsOfficialCliExecutableLease duplicate = lease.Duplicate();

		try
		{
			lease.Dispose();
			AssertWriteAndDeleteBlocked(sourcePath);
		}
		finally
		{
			lease.Dispose();
			duplicate.Dispose();
		}

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x03]);
		Assert.Equal(3, new FileInfo(sourcePath).Length);
	}

	[Fact]
	public void Stage_AfterLeaseDisposal_AllowsOfficialSourceUpdateAndRevalidation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		int inspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(_ =>
		{
			inspectionCount++;
			return CreateTrustedSignature();
		});

		using (WindowsOfficialCliExecutableLease lease = stager.Stage(sourcePath))
		{
			AssertWriteAndDeleteBlocked(sourcePath);
		}

		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x04, 0x05]);

		using WindowsOfficialCliExecutableLease updatedLease =
			stager.Stage(sourcePath);
		Assert.Equal(Path.GetFullPath(sourcePath), updatedLease.ExecutablePath);
		Assert.Equal(2, inspectionCount);
	}

	[Fact]
	public void Stage_WithUnsafeAcl_FailsClosedBeforeSignatureInspection()
	{
		AssertPathPolicyFailure(
			isCanonicalNonReparseFile: _ => true,
			isFixedDrivePath: _ => true,
			isPathAclSafe: _ => false);
	}

	[Fact]
	public void Stage_WithNonFixedPath_FailsClosedBeforeSignatureInspection()
	{
		AssertPathPolicyFailure(
			isCanonicalNonReparseFile: _ => true,
			isFixedDrivePath: _ => false,
			isPathAclSafe: _ => true);
	}

	[Fact]
	public void Stage_WithReparsePath_FailsClosedBeforeSignatureInspection()
	{
		AssertPathPolicyFailure(
			isCanonicalNonReparseFile: _ => false,
			isFixedDrivePath: _ => true,
			isPathAclSafe: _ => true);
	}

	[Fact]
	public void Stage_WithUnexpectedSigner_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		using GrokCliExecutableStager stager = CreateStager(_ =>
			new WindowsAuthenticodeInspection(
				WinVerifyTrustStatus: 0,
				SignerSubject: "CN=Unexpected Publisher",
				SignerThumbprint: new string('B', 40)));

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
	}

	[Fact]
	public void Stage_WithEmptySource_FailsClosedBeforeSignatureInspection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.Combine(temporaryDirectory.Path, "grok.exe");
		File.WriteAllBytes(sourcePath, []);
		int inspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(_ =>
		{
			inspectionCount++;
			return CreateTrustedSignature();
		});

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
		Assert.Equal(0, inspectionCount);
	}

	[Fact]
	public void Stage_WithOversizedSource_FailsClosedBeforeSignatureInspection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);

		using (FileStream stream = new(
			sourcePath,
			FileMode.Open,
			FileAccess.Write,
			FileShare.None))
		{
			stream.SetLength(
				GrokCliExecutableStager.MaximumExecutableSizeBytes + 1);
		}

		int inspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(_ =>
		{
			inspectionCount++;
			return CreateTrustedSignature();
		});

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
		Assert.Equal(0, inspectionCount);
	}

	[Fact]
	public void Stage_WhenPathIdentityChangesAfterSignature_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		WindowsOfficialCliExecutableTrustStamp original = new(
			Length: 4,
			CreationTime: 1,
			LastWriteTime: 2,
			ChangeTime: 3,
			VolumeSerialNumber: 4,
			FileIdLowPart: 5,
			FileIdHighPart: 6);
		WindowsOfficialCliExecutableTrustStamp changed = original with
		{
			FileIdLowPart = 7
		};
		int trustStampReadCount = 0;
		using WindowsOfficialCliExecutableStager stager = new(
			"X.AI LLC",
			_ => CreateTrustedSignature(),
			_ => true,
			_ => true,
			_ => true,
			_ => ++trustStampReadCount <= 3 ? original : changed);

		WindowsOfficialCliExecutableStagingException exception =
			Assert.Throws<WindowsOfficialCliExecutableStagingException>(() =>
			{
				using WindowsOfficialCliExecutableLease lease =
					stager.Stage(sourcePath);
			});

		Assert.Equal(
			WindowsOfficialCliExecutableStagingFailureReason.SourceChanged,
			exception.FailureReason);
	}

	[Fact]
	public void Stage_WhenAlreadyCanceled_DoesNotInspectSource()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		int inspectionCount = 0;
		using GrokCliExecutableStager stager = CreateStager(_ =>
		{
			inspectionCount++;
			return CreateTrustedSignature();
		});
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath, cancellation.Token);
		});
		Assert.Equal(0, inspectionCount);
	}

	[Fact]
	public void DirectSourceStagers_ContainNoExecutableWriteOrCopyApi()
	{
		foreach (string relativePath in new[]
		{
			Path.Combine(
				"src",
				"AiUsageDashboard.App",
				"Providers",
				"WindowsOfficialCliExecutableStager.cs"),
			Path.Combine(
				"src",
				"AiUsageDashboard.App",
				"Providers",
				"CodexCliExecutableStager.cs"),
			Path.Combine(
				"src",
				"AiUsageDashboard.App",
				"Providers",
				"GrokCliExecutableStager.cs")
		})
		{
			string source = File.ReadAllText(Path.Combine(
				RepositoryTestPaths.Root,
				relativePath));
			Assert.DoesNotContain("File.Copy(", source, StringComparison.Ordinal);
			Assert.DoesNotContain("File.Move(", source, StringComparison.Ordinal);
			Assert.DoesNotContain("FileMode.Create", source, StringComparison.Ordinal);
			Assert.DoesNotContain("FileAccess.Write", source, StringComparison.Ordinal);
			Assert.DoesNotContain("executables-v1", source, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ProductionStagers_UseProviderSpecificSourceAclValidation()
	{
		string providersDirectory = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Providers");
		string codexSource = File.ReadAllText(Path.Combine(
			providersDirectory,
			"CodexCliExecutableStager.cs"));
		string grokSource = File.ReadAllText(Path.Combine(
			providersDirectory,
			"GrokCliExecutableStager.cs"));
		string claudeSource = File.ReadAllText(Path.Combine(
			providersDirectory,
			"ClaudeCliExecutableStager.cs"));

		Assert.Contains(
			"IsResolvedExecutablePathAclSafe",
			codexSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"WindowsExecutablePathSecurity.IsPathAclSafe",
			grokSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"WindowsExecutablePathSecurity.IsPathAclSafe",
			claudeSource,
			StringComparison.Ordinal);
	}

	private static void AssertPathPolicyFailure(
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isFixedDrivePath,
		Func<string, bool> isPathAclSafe)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = CreateSource(temporaryDirectory);
		int inspectionCount = 0;
		using GrokCliExecutableStager stager = new(
			_ =>
			{
				inspectionCount++;
				return CreateTrustedSignature();
			},
			isCanonicalNonReparseFile,
			isFixedDrivePath,
			isPathAclSafe);

		Assert.Throws<GrokCliUntrustedException>(() =>
		{
			using WindowsOfficialCliExecutableLease lease =
				stager.Stage(sourcePath);
		});
		Assert.Equal(0, inspectionCount);
	}

	private static void AssertWriteAndDeleteBlocked(string sourcePath)
	{
		Exception? writeException = Record.Exception(() =>
		{
			using FileStream stream = new(
				sourcePath,
				FileMode.Open,
				FileAccess.Write,
				FileShare.ReadWrite | FileShare.Delete);
		});
		Exception? deleteException = Record.Exception(() => File.Delete(sourcePath));

		Assert.IsAssignableFrom<IOException>(writeException);
		Assert.IsAssignableFrom<IOException>(deleteException);
		Assert.True(File.Exists(sourcePath));
	}

	private static GrokCliExecutableStager CreateStager(
		Func<string, WindowsAuthenticodeInspection> inspectSignature)
	{
		return new GrokCliExecutableStager(
			inspectSignature,
			_ => true,
			_ => true,
			_ => true);
	}

	private static string CreateSource(TemporaryDirectory temporaryDirectory)
	{
		string sourcePath = Path.Combine(temporaryDirectory.Path, "grok.exe");
		File.WriteAllBytes(sourcePath, [0x4D, 0x5A, 0x01, 0x02]);
		return sourcePath;
	}

	private static WindowsAuthenticodeInspection CreateTrustedSignature()
	{
		return new WindowsAuthenticodeInspection(
			WinVerifyTrustStatus: 0,
			SignerSubject: ExactSignerSubject,
			SignerThumbprint: new string('A', 40));
	}
}
