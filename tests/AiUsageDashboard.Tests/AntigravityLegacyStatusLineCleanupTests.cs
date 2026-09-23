using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityLegacyStatusLineCleanupTests
{
	[Fact]
	public void RemoveOwnedArtifacts_WhenSettingsAndPrivateRootAreMissing_IsIdempotent()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"missing-private-root");

		Assert.True(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot));
	}

	[Fact]
	public void RemoveOwnedArtifacts_WhenSettingsAreMissingButPrivateRootExists_FailsClosed()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = CreatePrivateRoot(temporaryDirectory);
		string helperPath = CreatePrivateFile(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe",
			[1, 2, 3]);
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");

		Assert.False(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot));
		Assert.True(File.Exists(helperPath));
	}

	[Fact]
	public void RemoveOwnedArtifacts_WithCustomStatusLine_DoesNotMutateAnything()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = CreatePrivateRoot(temporaryDirectory);
		string helperPath = CreatePrivateFile(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe",
			[1, 2, 3]);
		string capturePath = CreatePrivateFile(
			privateRoot,
			AntigravityLegacyStatusLineCleanup.CaptureFileName,
			Encoding.UTF8.GetBytes("capture"));
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		const string Settings =
			"{\"theme\":\"dark\",\"statusLine\":{\"type\":\"command\",\"command\":\"my-tool.exe\"}}";
		File.WriteAllText(settingsPath, Settings);

		Assert.True(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot));
		Assert.Equal(Settings, File.ReadAllText(settingsPath));
		Assert.True(File.Exists(helperPath));
		Assert.True(File.Exists(capturePath));
	}

	[Fact]
	public void RemoveOwnedArtifacts_WithMalformedOwnedEnabled_FailsWithoutMutation()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = CreatePrivateRoot(temporaryDirectory);
		string helperPath = CreatePrivateFile(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe",
			[1, 2, 3]);
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string settings = CreateOwnedSettings(helperPath, "\"true\"");
		File.WriteAllText(settingsPath, settings);

		Assert.False(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot));
		Assert.Equal(settings, File.ReadAllText(settingsPath));
		Assert.True(File.Exists(helperPath));
	}

	[Fact]
	public void RemoveOwnedArtifacts_WithOwnedConfiguration_RemovesOnlyOwnedState()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = CreatePrivateRoot(temporaryDirectory);
		string helperPath = CreatePrivateFile(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe",
			[1, 2, 3]);
		_ = CreatePrivateFile(
			privateRoot,
			AntigravityLegacyStatusLineCleanup.CaptureFileName,
			Encoding.UTF8.GetBytes("capture"));
		_ = CreatePrivateFile(
			privateRoot,
			AntigravityLegacyStatusLineCleanup.CaptureLockFileName,
			Encoding.UTF8.GetBytes("lock"));
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		File.WriteAllText(settingsPath, CreateOwnedSettings(helperPath, "true"));

		Assert.True(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot));

		using JsonDocument settings = JsonDocument.Parse(
			File.ReadAllBytes(settingsPath));
		Assert.Equal(
			"dark",
			settings.RootElement.GetProperty("theme").GetString());
		Assert.False(settings.RootElement.TryGetProperty("statusLine", out _));
		Assert.False(Directory.Exists(privateRoot));
		Assert.DoesNotContain(
			Directory.EnumerateFiles(temporaryDirectory.Path),
			path => Path.GetFileName(path).StartsWith(
				".ai-usage-statusline-",
				StringComparison.Ordinal));
	}

	[Fact]
	public void RemoveOwnedArtifacts_WithOrphans_RequiresContentHashMatch()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = CreatePrivateRoot(temporaryDirectory);
		byte[] ownedBytes = [4, 3, 2, 1];
		string ownedHash = Convert.ToHexString(SHA256.HashData(ownedBytes))
			.ToLowerInvariant()[..16];
		string ownedOrphanPath = Path.Combine(
			privateRoot,
			$"AiUsageDashboard.AntigravityCapture-{ownedHash}.exe");
		File.WriteAllBytes(ownedOrphanPath, ownedBytes);
		string lookalikePath = CreatePrivateFile(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-ffffffffffffffff.exe",
			[9, 8, 7, 6]);
		Assert.NotEqual("ffffffffffffffff", ownedHash);
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		File.WriteAllText(settingsPath, "{\"theme\":\"dark\"}");

		Assert.True(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot));
		Assert.False(File.Exists(ownedOrphanPath));
		Assert.True(File.Exists(lookalikePath));
	}

	[Fact]
	public void RemoveOwnedArtifacts_WhenSettingsDisappearBeforeCleanup_PreservesArtifacts()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = CreatePrivateRoot(temporaryDirectory);
		string helperPath = CreatePrivateFile(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe",
			[1, 2, 3]);
		string capturePath = CreatePrivateFile(
			privateRoot,
			AntigravityLegacyStatusLineCleanup.CaptureFileName,
			Encoding.UTF8.GetBytes("capture"));
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		File.WriteAllText(settingsPath, CreateOwnedSettings(helperPath));

		Assert.False(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot,
			() => File.Delete(settingsPath)));
		Assert.False(File.Exists(settingsPath));
		Assert.True(File.Exists(helperPath));
		Assert.True(File.Exists(capturePath));
	}

	[Fact]
	public void RemoveOwnedArtifacts_WhenSettingsBecomeCustomBeforeCleanup_PreservesArtifacts()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = CreatePrivateRoot(temporaryDirectory);
		string helperPath = CreatePrivateFile(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe",
			[1, 2, 3]);
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		File.WriteAllText(settingsPath, CreateOwnedSettings(helperPath));
		const string CustomSettings =
			"{\"statusLine\":{\"type\":\"command\",\"command\":\"custom.exe\"}}";

		Assert.False(AntigravityLegacyStatusLineCleanup.TryRemoveOwnedArtifacts(
			settingsPath,
			privateRoot,
			() => File.WriteAllText(settingsPath, CustomSettings)));
		Assert.Equal(CustomSettings, File.ReadAllText(settingsPath));
		Assert.True(File.Exists(helperPath));
	}

	[Fact]
	public void AtomicReplace_WhenExternalWriterWinsBeforeValidation_DoesNotRollItBack()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		byte[] original = Encoding.UTF8.GetBytes("{\"value\":\"original\"}");
		byte[] updated = Encoding.UTF8.GetBytes("{\"value\":\"updated\"}");
		byte[] concurrent = Encoding.UTF8.GetBytes("{\"value\":\"concurrent\"}");
		File.WriteAllBytes(settingsPath, original);

		Assert.False(AntigravityLegacyStatusLineCleanup
			.TryReplaceSettingsAtomically(
				settingsPath,
				original,
				updated,
				beforeCommittedSecurityValidation: () =>
					File.WriteAllBytes(settingsPath, concurrent)));
		Assert.Equal(concurrent, File.ReadAllBytes(settingsPath));
		string backupPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.bak"));
		Assert.Equal(original, File.ReadAllBytes(backupPath));
	}

	[Fact]
	public void Rollback_WhenExternalWriterWinsAfterCheck_ReactivatesItsSettings()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string backupPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.backup.json");
		byte[] ourWrite = Encoding.UTF8.GetBytes("{\"value\":\"ours\"}");
		byte[] previous = Encoding.UTF8.GetBytes("{\"value\":\"previous\"}");
		byte[] concurrent = Encoding.UTF8.GetBytes("{\"value\":\"concurrent\"}");
		File.WriteAllBytes(settingsPath, ourWrite);
		File.WriteAllBytes(backupPath, previous);

		Assert.False(AntigravityLegacyStatusLineCleanup
			.TryRestoreBackupIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				beforeReplacement: () =>
					File.WriteAllBytes(settingsPath, concurrent)));
		Assert.Equal(concurrent, File.ReadAllBytes(settingsPath));
		Assert.Contains(
			Directory.EnumerateFiles(
				temporaryDirectory.Path,
				".ai-usage-statusline-*.recovery.bak"),
			path => File.ReadAllBytes(path).AsSpan().SequenceEqual(previous));
	}

	[Fact]
	public void PreservingRollback_WhenExternalWriterWinsAfterCheck_ReactivatesItsSettings()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string backupPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.backup.json");
		byte[] ourWrite = Encoding.UTF8.GetBytes("{\"value\":\"ours\"}");
		byte[] concurrent = Encoding.UTF8.GetBytes("{\"value\":\"concurrent\"}");
		const int LargeBackupLength = (1024 * 1024) + 1;
		File.WriteAllBytes(settingsPath, ourWrite);
		using (FileStream backup = new(
			backupPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
			backup.SetLength(LargeBackupLength);
		}

		Assert.False(AntigravityLegacyStatusLineCleanup
			.TryRestoreBackupPreservingRecoveryIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				beforeReplacement: () =>
					File.WriteAllBytes(settingsPath, concurrent)));
		Assert.Equal(concurrent, File.ReadAllBytes(settingsPath));
		Assert.Contains(
			Directory.EnumerateFiles(
				temporaryDirectory.Path,
				".ai-usage-statusline-*.recovery.bak"),
			path => new FileInfo(path).Length == LargeBackupLength);
	}

	[Fact]
	public void PreservingRollback_StopsAfterEightReactivationAttempts()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string backupPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.backup.json");
		byte[] ourWrite = Encoding.UTF8.GetBytes("{\"value\":\"ours\"}");
		byte[] firstConcurrent = Encoding.UTF8.GetBytes(
			"{\"value\":\"concurrent-0\"}");
		const int LargeBackupLength = (1024 * 1024) + 1;
		File.WriteAllBytes(settingsPath, ourWrite);
		using (FileStream backup = new(
			backupPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
			backup.SetLength(LargeBackupLength);
		}
		int reactivationAttempts = 0;
		byte[]? latestConcurrent = null;

		Assert.False(AntigravityLegacyStatusLineCleanup
			.TryRestoreBackupPreservingRecoveryIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				beforeReplacement: () =>
					File.WriteAllBytes(settingsPath, firstConcurrent),
				beforeReactivationReplacement: _ =>
				{
					reactivationAttempts++;
					latestConcurrent = Encoding.UTF8.GetBytes(
						$"{{\"value\":\"concurrent-{reactivationAttempts}\"}}");
					File.WriteAllBytes(settingsPath, latestConcurrent);
				}));

		Assert.Equal(8, reactivationAttempts);
		Assert.Equal(
			Encoding.UTF8.GetBytes("{\"value\":\"concurrent-7\"}"),
			File.ReadAllBytes(settingsPath));
		Assert.NotNull(latestConcurrent);
		Assert.Contains(
			Directory.EnumerateFiles(
				temporaryDirectory.Path,
				".ai-usage-statusline-*.recovery.bak"),
			path => File.ReadAllBytes(path)
				.AsSpan()
				.SequenceEqual(latestConcurrent));
	}

	[Fact]
	public void Rollback_WithUnexpectedCurrentAcl_DoesNotReplaceTarget()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string backupPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.backup.json");
		byte[] ourWrite = Encoding.UTF8.GetBytes("{\"value\":\"ours\"}");
		byte[] previous = Encoding.UTF8.GetBytes("{\"value\":\"previous\"}");
		File.WriteAllBytes(settingsPath, ourWrite);
		File.WriteAllBytes(backupPath, previous);
		string expectedCurrentSddl = ReadSecuritySddl(settingsPath);
		FileInfo settingsFile = new(settingsPath);
		FileSecurity unexpectedSecurity = settingsFile.GetAccessControl(
			AccessControlSections.Access |
			AccessControlSections.Owner |
			AccessControlSections.Group);
		unexpectedSecurity.SetAccessRuleProtection(
			isProtected: true,
			preserveInheritance: true);
		settingsFile.SetAccessControl(unexpectedSecurity);
		string unexpectedSddl = ReadSecuritySddl(settingsPath);
		bool replacementHookCalled = false;
		Assert.NotEqual(expectedCurrentSddl, unexpectedSddl);

		Assert.False(AntigravityLegacyStatusLineCleanup
			.TryRestoreBackupIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				expectedCurrentSddl,
				beforeReplacement: () => replacementHookCalled = true));
		Assert.False(replacementHookCalled);
		Assert.Equal(ourWrite, File.ReadAllBytes(settingsPath));
		Assert.Equal(unexpectedSddl, ReadSecuritySddl(settingsPath));
		Assert.Equal(previous, File.ReadAllBytes(backupPath));
	}

	[Fact]
	public void AtomicReplace_WhenReplacementThrowsAfterCommit_PreservesBothStates()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		byte[] original = Encoding.UTF8.GetBytes("{\"value\":\"original\"}");
		byte[] updated = Encoding.UTF8.GetBytes("{\"value\":\"updated\"}");
		File.WriteAllBytes(settingsPath, original);

		Assert.False(AntigravityLegacyStatusLineCleanup
			.TryReplaceSettingsAtomically(
				settingsPath,
				original,
				updated,
				afterReplacementBeforeCommitAcknowledged: () =>
					throw new IOException("Simulated post-replacement failure.")));
		Assert.Equal(original, File.ReadAllBytes(settingsPath));
		string recoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.recovery.bak"));
		Assert.Equal(updated, File.ReadAllBytes(recoveryPath));
		Assert.DoesNotContain(
			Directory.EnumerateFiles(
				temporaryDirectory.Path,
				".ai-usage-statusline-*.bak"),
			path => !path.EndsWith(
				".recovery.bak",
				StringComparison.Ordinal));
	}

	[Fact]
	public void AtomicReplace_WhenBackupCannotBeDeleted_DoesNotReportSuccess()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		byte[] original = Encoding.UTF8.GetBytes("{\"value\":\"original\"}");
		byte[] updated = Encoding.UTF8.GetBytes("{\"value\":\"updated\"}");
		File.WriteAllBytes(settingsPath, original);
		FileStream? backupLock = null;
		string? backupPath = null;

		try
		{
			Assert.False(AntigravityLegacyStatusLineCleanup
				.TryReplaceSettingsAtomically(
					settingsPath,
					original,
					updated,
					beforeBackupDeletion: path =>
					{
						backupPath = path;
						backupLock = new FileStream(
							path,
							FileMode.Open,
							FileAccess.Read,
							FileShare.Read);
					}));
			Assert.Equal(updated, File.ReadAllBytes(settingsPath));
			Assert.NotNull(backupPath);
			Assert.True(File.Exists(backupPath));
			Assert.Equal(original, File.ReadAllBytes(backupPath));
		}
		finally
		{
			backupLock?.Dispose();
			if (backupPath is not null)
			{
				File.Delete(backupPath);
			}
		}
	}

	private static string CreatePrivateRoot(
		TemporaryDirectory temporaryDirectory)
	{
		string applicationRoot = Path.Combine(
			temporaryDirectory.Path,
			"AiUsageDashboard");
		Directory.CreateDirectory(applicationRoot);
		string privateRoot = Path.Combine(
			applicationRoot,
			"private",
			"antigravity-statusline");

		Assert.True(AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
			privateRoot,
			out _,
			out AntigravityPrivateKeyDirectoryFailureReason failureReason));
		Assert.Equal(
			AntigravityPrivateKeyDirectoryFailureReason.None,
			failureReason);
		return privateRoot;
	}

	private static string CreatePrivateFile(
		string privateRoot,
		string fileName,
		byte[] contents)
	{
		string path = Path.Combine(privateRoot, fileName);
		File.WriteAllBytes(path, contents);
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(path));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateFile(path));
		return path;
	}

	private static string CreateOwnedSettings(
		string helperPath,
		string enabledJson = "true")
	{
		string command = $"call \"{helperPath}\" " +
			AntigravityLegacyStatusLineCleanup.OwnershipMarker;
		return "{\"theme\":\"dark\",\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(command) +
			$",\"enabled\":{enabledJson}}}}}";
	}

	private static string ReadSecuritySddl(string path)
	{
		return new FileInfo(path)
			.GetAccessControl(
				AccessControlSections.Access |
				AccessControlSections.Owner |
				AccessControlSections.Group)
			.GetSecurityDescriptorSddlForm(
				AccessControlSections.Access |
				AccessControlSections.Owner |
				AccessControlSections.Group);
	}
}
