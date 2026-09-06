using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityStatusLineIntegrationTests
{
	private const uint DaclSecurityInformation = 0x00000004;
	private const AccessControlSections OwnerGroupAndAccess =
		AccessControlSections.Access |
		AccessControlSections.Owner |
		AccessControlSections.Group;
	private const string PrivateRoot =
		@"C:\Users\Tester\AppData\Local\AiUsageDashboard\private\antigravity-statusline";
	private const string HelperPath = PrivateRoot +
		@"\AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe";
	private const string Command = HelperPath + " " +
		AntigravityStatusLineIntegration.OwnershipMarker;

	[Fact]
	public void CreateSettings_WhenStatusLineIsAbsent_PreservesUnknownProperties()
	{
		byte[] original = Encoding.UTF8.GetBytes(
			"{\"theme\":\"dark\",\"nested\":{\"keep\":true}}");

		bool succeeded =
			AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
				original,
				Command,
				PrivateRoot,
				out byte[]? updated,
				out bool alreadyInstalled,
				out bool custom);

		Assert.True(succeeded);
		Assert.False(alreadyInstalled);
		Assert.False(custom);
		using JsonDocument document = JsonDocument.Parse(updated!);
		Assert.Equal("dark", document.RootElement.GetProperty("theme").GetString());
		Assert.True(document.RootElement
			.GetProperty("nested")
			.GetProperty("keep")
			.GetBoolean());
		JsonElement statusLine = document.RootElement.GetProperty("statusLine");
		Assert.Equal("command", statusLine.GetProperty("type").GetString());
		Assert.Equal(Command, statusLine.GetProperty("command").GetString());
		Assert.True(statusLine.GetProperty("enabled").GetBoolean());
		Assert.True(statusLine.GetProperty("stack_with_default").GetBoolean());
	}

	[Fact]
	public void CreateSettings_WithCustomStatusLine_DoesNotOverwriteIt()
	{
		byte[] original = Encoding.UTF8.GetBytes(
			"{\"statusLine\":{\"type\":\"command\",\"command\":\"my-tool.exe\"}}");

		bool succeeded =
			AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
				original,
				Command,
				PrivateRoot,
				out byte[]? updated,
				out bool alreadyInstalled,
				out bool custom);

		Assert.False(succeeded);
		Assert.Null(updated);
		Assert.False(alreadyInstalled);
		Assert.True(custom);
	}

	[Fact]
	public void CreateSettings_WithOwnedDisabledStatusLine_UpdatesCommandButKeepsDisabled()
	{
		const string OldHelper = PrivateRoot +
			@"\AiUsageDashboard.AntigravityCapture-fedcba9876543210.exe";
		string oldCommand = OldHelper + " " +
			AntigravityStatusLineIntegration.OwnershipMarker;
		byte[] original = Encoding.UTF8.GetBytes(
			"{\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(oldCommand) +
			",\"enabled\":false,\"stack_with_default\":true,\"padding\":2}}");

		Assert.True(AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
			original,
			Command,
			PrivateRoot,
			out byte[]? updated,
			out bool alreadyInstalled,
			out bool custom));
		Assert.False(alreadyInstalled);
		Assert.False(custom);
		using JsonDocument document = JsonDocument.Parse(updated!);
		JsonElement statusLine = document.RootElement.GetProperty("statusLine");
		Assert.Equal(Command, statusLine.GetProperty("command").GetString());
		Assert.False(statusLine.GetProperty("enabled").GetBoolean());
		Assert.Equal(2, statusLine.GetProperty("padding").GetInt32());
	}

	[Theory]
	[InlineData("null")]
	[InlineData("\"false\"")]
	[InlineData("1")]
	[InlineData("{}")]
	public void CreateSettings_WithMalformedOwnedEnabled_FailsClosed(
		string enabledJson)
	{
		byte[] original = Encoding.UTF8.GetBytes(
			"{\"statusLine\":{\"type\":\"command\",\"command\":" +
				JsonSerializer.Serialize(Command) +
				$",\"enabled\":{enabledJson}}}}}");

		Assert.False(AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
			original,
			Command,
			PrivateRoot,
			out byte[]? updated,
			out bool alreadyInstalled,
			out bool custom));
		Assert.Null(updated);
		Assert.False(alreadyInstalled);
		Assert.False(custom);
	}

	[Fact]
	public void CreateSettings_WithDuplicateProperty_FailsClosed()
	{
		byte[] original = Encoding.UTF8.GetBytes("{\"theme\":1,\"theme\":2}");

		Assert.False(AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
			original,
			Command,
			PrivateRoot,
			out _,
			out _,
			out _));
	}

	[Fact]
	public void RemoveSettings_WithOwnedStatusLine_PreservesOtherProperties()
	{
		byte[] original = Encoding.UTF8.GetBytes(
			"{\"theme\":\"dark\",\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(Command) +
			"},\"nested\":{\"keep\":true}}");

		Assert.True(AntigravityStatusLineIntegration
			.TryCreateSettingsWithoutOwnedStatusLine(
				original,
				PrivateRoot,
				out byte[]? updated));
		using JsonDocument document = JsonDocument.Parse(updated!);
		Assert.False(document.RootElement.TryGetProperty("statusLine", out _));
		Assert.Equal(
			"dark",
			document.RootElement.GetProperty("theme").GetString());
		Assert.True(document.RootElement
			.GetProperty("nested")
			.GetProperty("keep")
			.GetBoolean());
	}

	[Theory]
	[InlineData("{\"theme\":\"dark\"}")]
	[InlineData("{\"statusLine\":{\"type\":\"command\",\"command\":\"my-tool.exe\"}}")]
	[InlineData("{\"statusLine\":1}")]
	public void RemoveSettings_WithoutOwnedStatusLine_FailsClosed(
		string json)
	{
		Assert.False(AntigravityStatusLineIntegration
			.TryCreateSettingsWithoutOwnedStatusLine(
				Encoding.UTF8.GetBytes(json),
				PrivateRoot,
				out byte[]? updated));
		Assert.Null(updated);
	}

	[Fact]
	public void Rollback_WhenDestinationChangesAfterCheck_PreservesRecoveryCopy()
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
		string currentSddl = ReadSettingsSecuritySddl(settingsPath);
		string previousSddl = ApplyLegacyInheritedDacl(backupPath);

		Assert.True(AntigravityStatusLineIntegration
			.TryRestoreBackupIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				expectedCurrentSddl: currentSddl,
				beforeReplacement: () =>
					File.WriteAllBytes(settingsPath, concurrent)));

		Assert.Equal(previous, File.ReadAllBytes(settingsPath));
		Assert.True(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(
				previousSddl,
				ReadSettingsSecuritySddl(settingsPath)));
		Assert.False(File.Exists(backupPath));
		string recoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.recovery.bak"));
		Assert.Equal(concurrent, File.ReadAllBytes(recoveryPath));
	}

	[Fact]
	public void Rollback_WhenDestinationDaclChangesAfterCheck_PreservesRecoveryCopy()
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
		string currentSddl = ReadSettingsSecuritySddl(settingsPath);
		string previousSddl = ApplyLegacyInheritedDacl(backupPath);
		string? concurrentSddl = null;

		Assert.False(AntigravityStatusLineIntegration
			.TryRestoreBackupIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				expectedCurrentSddl: currentSddl,
				beforeReplacement: () =>
				{
					FileInfo settingsFile = new(settingsPath);
					FileSecurity security = settingsFile.GetAccessControl(
						OwnerGroupAndAccess);
					security.SetAccessRuleProtection(
						isProtected: true,
						preserveInheritance: true);
					settingsFile.SetAccessControl(security);
					concurrentSddl = ReadSettingsSecuritySddl(settingsPath);
				}));

		Assert.NotNull(concurrentSddl);
		Assert.NotEqual(previousSddl, concurrentSddl);
		Assert.Equal(previous, File.ReadAllBytes(settingsPath));
		Assert.Equal(concurrentSddl, ReadSettingsSecuritySddl(settingsPath));
		Assert.False(File.Exists(backupPath));
		string recoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.recovery.bak"));
		Assert.Equal(ourWrite, File.ReadAllBytes(recoveryPath));
		Assert.Equal(concurrentSddl, ReadSettingsSecuritySddl(recoveryPath));
	}

	[Fact]
	public void AtomicReplace_WhenCommittedDaclChangesBeforeValidation_FailsWithoutOverwritingIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		byte[] originalBytes = Encoding.UTF8.GetBytes(
			"{\"value\":\"original\"}");
		byte[] updatedBytes = Encoding.UTF8.GetBytes(
			"{\"value\":\"updated\"}");
		File.WriteAllBytes(settingsPath, originalBytes);
		FileInfo settingsFile = new(settingsPath);
		FileSecurity protectedSecurity = settingsFile.GetAccessControl(
			OwnerGroupAndAccess);
		protectedSecurity.SetAccessRuleProtection(
			isProtected: true,
			preserveInheritance: true);
		settingsFile.SetAccessControl(protectedSecurity);
		string originalSddl = ReadSettingsSecuritySddl(settingsPath);
		string? concurrentSddl = null;

		Assert.False(AntigravityStatusLineIntegration
			.TryReplaceSettingsAtomically(
				settingsPath,
				originalBytes,
				updatedBytes,
				beforeCommittedSecurityValidation: () =>
				{
					RawSecurityDescriptor committed = new(
						ReadSettingsSecuritySddl(settingsPath));
					RawAcl committedDacl = committed.DiscretionaryAcl ??
						throw new InvalidOperationException(
							"Test fixture must contain a DACL.");
					RawSecurityDescriptor concurrent = new(
						committed.ControlFlags |
							ControlFlags.DiscretionaryAclProtected,
						committed.Owner,
						committed.Group,
						systemAcl: null,
						DuplicateAcl(committedDacl));
					ApplyRawDacl(settingsPath, concurrent);
					concurrentSddl = ReadSettingsSecuritySddl(settingsPath);
				}));

		Assert.NotNull(concurrentSddl);
		Assert.NotEqual(originalSddl, concurrentSddl);
		Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
		string restoredSddl = ReadSettingsSecuritySddl(settingsPath);
		Assert.True(
			SecurityMatchesIgnoringAutoInheritedFlag(
				concurrentSddl,
				restoredSddl),
			DescribeSecurityComparison(concurrentSddl, restoredSddl));
		string recoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.recovery.bak"));
		Assert.Equal(updatedBytes, File.ReadAllBytes(recoveryPath));
		Assert.Equal(concurrentSddl, ReadSettingsSecuritySddl(recoveryPath));
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.tmp"));
	}

	[Fact]
	public void Rollback_WhenDestinationIsOurWrite_DeletesRecoveryCopy()
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
		FileInfo backupFile = new(backupPath);
		FileSecurity protectedBackup = backupFile.GetAccessControl(
			OwnerGroupAndAccess);
		protectedBackup.SetAccessRuleProtection(
			isProtected: true,
			preserveInheritance: true);
		backupFile.SetAccessControl(protectedBackup);
		string currentSddl = ReadSettingsSecuritySddl(settingsPath);
		string previousSddl = ReadSettingsSecuritySddl(backupPath);

		Assert.True(AntigravityStatusLineIntegration
			.TryRestoreBackupIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				expectedCurrentSddl: currentSddl));

		Assert.Equal(previous, File.ReadAllBytes(settingsPath));
		Assert.Equal(previousSddl, ReadSettingsSecuritySddl(settingsPath));
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.recovery.bak"));
	}

	[Fact]
	public void Rollback_WhenBackupExceedsSnapshotLimit_RestoresItAndPreservesRecovery()
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
		const int OversizedSettingsLength = (1024 * 1024) + 1;
		File.WriteAllBytes(settingsPath, ourWrite);
		using (FileStream backup = new(
			backupPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
			backup.SetLength(OversizedSettingsLength);
		}
		string previousSddl = ApplyLegacyInheritedDacl(backupPath);

		Assert.True(AntigravityStatusLineIntegration
			.TryRestoreBackupPreservingRecoveryIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath));

		Assert.Equal(OversizedSettingsLength, new FileInfo(settingsPath).Length);
		Assert.True(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(
				previousSddl,
				ReadSettingsSecuritySddl(settingsPath)));
		Assert.False(File.Exists(backupPath));
		string recoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.recovery.bak"));
		Assert.Equal(ourWrite, File.ReadAllBytes(recoveryPath));
	}

	[Fact]
	public void Rollback_WhenOversizedRestoredTargetChangesBeforeValidation_DoesNotRepairIt()
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
		byte[] concurrent = Encoding.UTF8.GetBytes(
			"{\"value\":\"concurrent\"}");
		const int OversizedSettingsLength = (1024 * 1024) + 1;
		File.WriteAllBytes(settingsPath, ourWrite);
		using (FileStream backup = new(
			backupPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
			backup.SetLength(OversizedSettingsLength);
		}
		ApplyLegacyInheritedDacl(backupPath);
		string? concurrentSddl = null;

		Assert.False(AntigravityStatusLineIntegration
			.TryRestoreBackupPreservingRecoveryIfTargetMatches(
				settingsPath,
				ourWrite,
				backupPath,
				beforeRestoredSecurityValidation: () =>
				{
					File.WriteAllBytes(settingsPath, concurrent);
					FileInfo settingsFile = new(settingsPath);
					FileSecurity security = settingsFile.GetAccessControl(
						OwnerGroupAndAccess);
					security.SetAccessRuleProtection(
						isProtected: true,
						preserveInheritance: true);
					settingsFile.SetAccessControl(security);
					concurrentSddl = ReadSettingsSecuritySddl(settingsPath);
				}));

		Assert.NotNull(concurrentSddl);
		Assert.Equal(concurrent, File.ReadAllBytes(settingsPath));
		Assert.Equal(concurrentSddl, ReadSettingsSecuritySddl(settingsPath));
		Assert.False(File.Exists(backupPath));
		string recoveryPath = Assert.Single(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			".ai-usage-statusline-*.recovery.bak"));
		Assert.Equal(ourWrite, File.ReadAllBytes(recoveryPath));
	}

	[Fact]
	public void BuildOwnedCommand_WithUnicodeAndNoWhitespace_UsesBarePathAndRoundTrips()
	{
		const string privateRoot =
			@"C:\測試資料\無空白\AiUsageDashboard\private\antigravity-statusline";
		const string helperPath = privateRoot +
			@"\AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe";
		string command =
			AntigravityStatusLineIntegration.BuildOwnedCommand(helperPath);
		byte[] original = Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}");

		Assert.Equal(
			$"{helperPath} " +
				AntigravityStatusLineIntegration.OwnershipMarker,
			command);
		Assert.True(AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
			original,
			command,
			privateRoot,
			out byte[]? updated,
			out bool firstAlreadyInstalled,
			out bool firstCustom));
		Assert.False(firstAlreadyInstalled);
		Assert.False(firstCustom);
		Assert.True(AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
			updated!,
			command,
			privateRoot,
			out _,
			out bool secondAlreadyInstalled,
			out bool secondCustom));
		Assert.True(secondAlreadyInstalled);
		Assert.False(secondCustom);
	}

	[Fact]
	public void CreateSettings_WithLegacyQuotedOwnedCommand_MigratesToBareCommand()
	{
		string legacyCommand = $"\"{HelperPath}\" " +
			AntigravityStatusLineIntegration.OwnershipMarker;
		string currentCommand =
			AntigravityStatusLineIntegration.BuildOwnedCommand(HelperPath);
		byte[] original = Encoding.UTF8.GetBytes(
			"{\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(legacyCommand) + "}}");

		Assert.True(AntigravityStatusLineIntegration.TryCreateUpdatedSettings(
			original,
			currentCommand,
			PrivateRoot,
			out byte[]? updated,
			out bool alreadyInstalled,
			out bool custom));
		Assert.False(alreadyInstalled);
		Assert.False(custom);
		using JsonDocument document = JsonDocument.Parse(updated!);
		Assert.Equal(
			currentCommand,
			document.RootElement
				.GetProperty("statusLine")
				.GetProperty("command")
				.GetString());
	}

	[Theory]
	[InlineData(@"C:\測試資料\has space\helper.exe")]
	[InlineData(@"C:\測試資料\name(parentheses)\helper.exe")]
	[InlineData(@"C:\測試資料\name,comma\helper.exe")]
	[InlineData(@"C:\測試資料\name=equals\helper.exe")]
	public void BuildOwnedCommand_WithSafeQuotedCharacters_UsesCall(
		string helperPath)
	{
		Assert.Equal(
			$"call \"{helperPath}\" " +
				AntigravityStatusLineIntegration.OwnershipMarker,
			AntigravityStatusLineIntegration.BuildOwnedCommand(helperPath));
	}

	[Fact]
	[Trait("Category", "WindowsIntegration")]
	public async Task BuildOwnedCommand_WithQuotedPath_ExecutesThroughCmdBoundary()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private storage");
		Directory.CreateDirectory(privateRoot);
		string helperPath = Path.Combine(privateRoot, "capture-helper.cmd");
		File.WriteAllText(
			helperPath,
			$"@if \"%~1\"==\"{AntigravityStatusLineIntegration.OwnershipMarker}\" exit /b 37\r\n" +
				"@exit /b 91\r\n",
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		string command =
			AntigravityStatusLineIntegration.BuildOwnedCommand(helperPath);
		using Process process = new()
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
				WorkingDirectory = temporaryDirectory.Path,
				UseShellExecute = false,
				CreateNoWindow = true,
				// Shell integrations pass one raw command string after /c. Using
				// ArgumentList would apply argv escaping that cmd.exe does not parse
				// as CommandLineToArgvW syntax and would test a different boundary.
				Arguments = $"/d /s /c \"{command}\""
			}
		};

		Assert.True(process.Start());
		await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(37, process.ExitCode);
	}

	[Theory]
	[InlineData(@"C:\測試資料\name%TEMP%\helper.exe")]
	[InlineData(@"C:\測試資料\name!var!\helper.exe")]
	[InlineData(@"C:\測試資料\name$env\helper.exe")]
	[InlineData(@"C:\測試資料\name&whoami\helper.exe")]
	[InlineData("C:\\測試資料\\name`whoami\\helper.exe")]
	public void BuildOwnedCommand_WithShellMetacharacters_Rejects(
		string helperPath)
	{
		Assert.Throws<ArgumentException>(() =>
			AntigravityStatusLineIntegration.BuildOwnedCommand(helperPath));
	}

	[Fact]
	public void EnsureInstalled_WithCustomStatusLine_DoesNotDeployHelper()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string missingHelperPath = Path.Combine(
			temporaryDirectory.Path,
			AntigravityStatusLineIntegration.PackagedHelperFileName);
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private storage");
		File.WriteAllText(
			settingsPath,
			"{\"statusLine\":{\"type\":\"command\",\"command\":\"my-tool.exe\"}}");

		AntigravityStatusLineIntegrationResult result =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				missingHelperPath,
				settingsPath,
				privateRoot);

		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.ExistingCustomStatusLine,
			result.Status);
		Assert.False(Directory.Exists(privateRoot));
	}

	[Fact]
	public void EnsureInstalled_WithOwnedDisabledStatusLine_DoesNotDeployHelper()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string missingHelperPath = Path.Combine(
			temporaryDirectory.Path,
			AntigravityStatusLineIntegration.PackagedHelperFileName);
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"私人 storage");
		string ownedHelper = Path.Combine(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe");
		string command = $"\"{ownedHelper}\" " +
			AntigravityStatusLineIntegration.OwnershipMarker;
		File.WriteAllText(
			settingsPath,
			"{\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(command) +
			",\"enabled\":false}}");

		AntigravityStatusLineIntegrationResult result =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				missingHelperPath,
				settingsPath,
				privateRoot);

		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.AlreadyInstalled,
			result.Status);
		Assert.False(Directory.Exists(privateRoot));
	}

	[Fact]
	public void EnsureInstalled_WhenPrivatePathRequiresQuoting_UsesCallCommand()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string packageDirectory = Path.Combine(temporaryDirectory.Path, "package");
		Directory.CreateDirectory(packageDirectory);
		string helperPath = Path.Combine(
			packageDirectory,
			AntigravityStatusLineIntegration.PackagedHelperFileName);
		File.WriteAllBytes(helperPath, new byte[] { 1, 2, 3, 4 });
		string settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
		const string originalSettings = "{\"theme\":\"dark\"}";
		File.WriteAllText(settingsPath, originalSettings);
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			"private storage");

		AntigravityStatusLineIntegrationResult result =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot);

		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			result.Status);
		Assert.True(Directory.Exists(privateRoot));
		using JsonDocument settings = JsonDocument.Parse(
			File.ReadAllBytes(settingsPath));
		string command = settings.RootElement
			.GetProperty("statusLine")
			.GetProperty("command")
			.GetString()!;
		Assert.StartsWith("call \"", command, StringComparison.Ordinal);
		Assert.EndsWith(
			$"\" {AntigravityStatusLineIntegration.OwnershipMarker}",
			command,
			StringComparison.Ordinal);
		Assert.True(AntigravityStatusLineIntegration
			.IsOwnedStatusLineConfigured(settingsPath, privateRoot));
	}

	[Fact]
	public void EnsureInstalled_WithMalformedOwnedEnabled_FailsWithoutMutation()
	{
		using TemporaryDirectory temporaryDirectory = new();
		(string helperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		string ownedHelperPath = Path.Combine(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-0123456789abcdef.exe");
		string malformedSettings =
			"{\"statusLine\":{\"type\":\"command\",\"command\":" +
				JsonSerializer.Serialize(
					AntigravityStatusLineIntegration.BuildOwnedCommand(
						ownedHelperPath)) +
				",\"enabled\":\"false\"}}";
		File.WriteAllText(settingsPath, malformedSettings);

		AntigravityStatusLineIntegrationResult result =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot);

		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Failed,
			result.Status);
		Assert.Equal(malformedSettings, File.ReadAllText(settingsPath));
		Assert.False(Directory.Exists(privateRoot));
	}

	[Fact]
	public void ApplyReplacementSettingsSecurity_WithExplicitSourceDacl_CopiesIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.Combine(temporaryDirectory.Path, "source.json");
		string replacementPath = Path.Combine(
			temporaryDirectory.Path,
			"replacement.json");
		File.WriteAllText(sourcePath, "{}");
		File.WriteAllText(replacementPath, "{}");
		const AccessControlSections Sections =
			AccessControlSections.Access |
			AccessControlSections.Owner |
			AccessControlSections.Group;
		FileInfo sourceFile = new(sourcePath);
		FileSecurity explicitSecurity = sourceFile.GetAccessControl(Sections);
		explicitSecurity.SetAccessRuleProtection(
			isProtected: true,
			preserveInheritance: true);
		sourceFile.SetAccessControl(explicitSecurity);
		string sourceSddl = sourceFile
			.GetAccessControl(Sections)
			.GetSecurityDescriptorSddlForm(Sections);
		string inheritedReplacementSddl = new FileInfo(replacementPath)
			.GetAccessControl(Sections)
			.GetSecurityDescriptorSddlForm(Sections);
		Assert.NotEqual(sourceSddl, inheritedReplacementSddl);

		Assert.True(AntigravityStatusLineIntegration
			.TryApplyReplacementSettingsSecurity(replacementPath, sourceSddl));

		string preparedReplacementSddl = new FileInfo(replacementPath)
			.GetAccessControl(Sections)
			.GetSecurityDescriptorSddlForm(Sections);
		Assert.Equal(sourceSddl, preparedReplacementSddl);
	}

	[Fact]
	public void ApplyReplacementSettingsSecurity_WithDifferentOwner_FailsClosed()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string replacementPath = Path.Combine(
			temporaryDirectory.Path,
			"replacement.json");
		File.WriteAllText(replacementPath, "{}");
		const AccessControlSections Sections =
			AccessControlSections.Access |
			AccessControlSections.Owner |
			AccessControlSections.Group;
		FileInfo replacementFile = new(replacementPath);
		FileSecurity actualSecurity = replacementFile.GetAccessControl(Sections);
		SecurityIdentifier actualOwner = Assert.IsType<SecurityIdentifier>(
			actualSecurity.GetOwner(typeof(SecurityIdentifier)));
		SecurityIdentifier differentOwner = new(
			actualOwner.IsWellKnown(WellKnownSidType.LocalSystemSid)
				? WellKnownSidType.BuiltinAdministratorsSid
				: WellKnownSidType.LocalSystemSid,
			null);
		FileSecurity mismatchedSecurity = new();
		mismatchedSecurity.SetSecurityDescriptorBinaryForm(
			actualSecurity.GetSecurityDescriptorBinaryForm(),
			Sections);
		mismatchedSecurity.SetOwner(differentOwner);
		string mismatchedSddl = mismatchedSecurity
			.GetSecurityDescriptorSddlForm(Sections);

		Assert.False(AntigravityStatusLineIntegration
			.TryApplyReplacementSettingsSecurity(
				replacementPath,
				mismatchedSddl));

		SecurityIdentifier committedOwner = Assert.IsType<SecurityIdentifier>(
			replacementFile
				.GetAccessControl(AccessControlSections.Owner)
				.GetOwner(typeof(SecurityIdentifier)));
		Assert.Equal(actualOwner, committedOwner);
	}

	[Fact]
	public void ApplyReplacementSettingsSecurity_WithExactLegacyInheritedDacl_DoesNotRewriteIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string sourcePath = Path.Combine(temporaryDirectory.Path, "source.json");
		string replacementPath = Path.Combine(
			temporaryDirectory.Path,
			"replacement.json");
		File.WriteAllText(sourcePath, "{}");
		File.WriteAllText(replacementPath, "{}");
		string sourceSddl = ApplyLegacyInheritedDacl(sourcePath);
		string replacementSddl = ApplyLegacyInheritedDacl(replacementPath);
		Assert.Equal(sourceSddl, replacementSddl);

		Assert.True(AntigravityStatusLineIntegration
			.TryApplyReplacementSettingsSecurity(replacementPath, sourceSddl));

		string preparedSddl = new FileInfo(replacementPath)
			.GetAccessControl(OwnerGroupAndAccess)
			.GetSecurityDescriptorSddlForm(OwnerGroupAndAccess);
		Assert.Equal(sourceSddl, preparedSddl);
	}

	[Fact]
	public void ReplacementSettingsSecurity_WithSameDirectoryFiles_PreservesAcceptedMetadata()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		string replacementPath = Path.Combine(
			temporaryDirectory.Path,
			".replacement.tmp");
		string backupPath = Path.Combine(
			temporaryDirectory.Path,
			".settings.backup");
		File.WriteAllText(settingsPath, "old");
		byte[] replacementBytes = Encoding.UTF8.GetBytes("new");
		using (FileStream output = new(
			replacementPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			4096,
			FileOptions.WriteThrough))
		{
			output.Write(replacementBytes);
			output.Flush(flushToDisk: true);
		}

		string originalSddl = ReadSettingsSecuritySddl(settingsPath);
		string beforePreparationSddl =
			ReadSettingsSecuritySddl(replacementPath);
		bool prepared = AntigravityStatusLineIntegration
			.TryApplyReplacementSettingsSecurity(
				replacementPath,
				originalSddl);
		string afterPreparationSddl =
			ReadSettingsSecuritySddl(replacementPath);
		Assert.True(
			prepared,
			"Security preparation failed. " +
			$"Before: {DescribeSecurityComparison(originalSddl, beforePreparationSddl)}. " +
			$"After: {DescribeSecurityComparison(originalSddl, afterPreparationSddl)}.");

		Exception? replacementException = null;

		try
		{
			File.Replace(
				replacementPath,
				settingsPath,
				backupPath,
				ignoreMetadataErrors: false);
		}
		catch (Exception ex)
		{
			replacementException = ex;
		}

		Assert.True(
			replacementException is null,
			"File.Replace failed after security preparation. " +
			$"Type={replacementException?.GetType().Name}; " +
			$"HResult=0x{replacementException?.HResult:X8}; " +
			$"Prepared: {DescribeSecurityComparison(originalSddl, afterPreparationSddl)}.");

		string backupSddl = ReadSettingsSecuritySddl(backupPath);
		Assert.Equal(originalSddl, backupSddl);
		Assert.True(
			AntigravityStatusLineIntegration
				.TryValidateAndRepairCommittedSettingsSecurity(
					settingsPath,
					replacementBytes,
					backupSddl),
			"Committed security could not be validated or repaired. " +
			DescribeSecurityComparison(
				backupSddl,
				ReadSettingsSecuritySddl(settingsPath)));

		string committedSddl = ReadSettingsSecuritySddl(settingsPath);
		Assert.True(
			AntigravityStatusLineIntegration
				.SettingsSecurityMatchesAfterReplacement(
					backupSddl,
					committedSddl),
			"Committed security was not accepted. " +
			DescribeSecurityComparison(backupSddl, committedSddl));
		Assert.Equal("new", File.ReadAllText(settingsPath));
		Assert.Equal("old", File.ReadAllText(backupPath));
	}

	[Fact]
	public void ValidateCommittedSettingsSecurity_WithUnknownDaclMismatch_DoesNotMutateIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		byte[] settingsBytes = Encoding.UTF8.GetBytes("{}");
		File.WriteAllBytes(settingsPath, settingsBytes);
		string originalSddl = ApplyLegacyInheritedDacl(settingsPath);
		RawSecurityDescriptor original = new(originalSddl);
		RawAcl originalDacl = original.DiscretionaryAcl ??
			throw new InvalidOperationException("Test fixture must contain a DACL.");
		RawAcl duplicatedDacl = DuplicateAcl(originalDacl);

		RawSecurityDescriptor duplicated = new(
			original.ControlFlags |
				ControlFlags.DiscretionaryAclProtected,
			original.Owner,
			original.Group,
			systemAcl: null,
			duplicatedDacl);
		ApplyRawDacl(settingsPath, duplicated);
		string duplicatedSddl = ReadSettingsSecuritySddl(settingsPath);
		Assert.False(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(
				originalSddl,
				duplicatedSddl));
		Assert.False(AntigravityStatusLineIntegration
			.TryValidateAndRepairCommittedSettingsSecurity(
				settingsPath,
				Encoding.UTF8.GetBytes("{\"changed\":true}"),
				originalSddl));
		Assert.Equal(duplicatedSddl, ReadSettingsSecuritySddl(settingsPath));

		Assert.False(AntigravityStatusLineIntegration
			.TryValidateAndRepairCommittedSettingsSecurity(
				settingsPath,
				settingsBytes,
				originalSddl));

		Assert.Equal(duplicatedSddl, ReadSettingsSecuritySddl(settingsPath));
		Assert.Equal(settingsBytes, File.ReadAllBytes(settingsPath));
	}

	[Fact]
	public void ValidateCommittedSettingsSecurity_WithAuthoritativeRollbackRepair_RepairsPinnedFile()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string settingsPath = Path.Combine(
			temporaryDirectory.Path,
			"settings.json");
		byte[] settingsBytes = Encoding.UTF8.GetBytes("{}");
		File.WriteAllBytes(settingsPath, settingsBytes);
		string originalSddl = ApplyLegacyInheritedDacl(settingsPath);
		RawSecurityDescriptor original = new(originalSddl);
		RawAcl originalDacl = original.DiscretionaryAcl ??
			throw new InvalidOperationException(
				"Test fixture must contain a DACL.");
		RawSecurityDescriptor expanded = new(
			original.ControlFlags |
				ControlFlags.DiscretionaryAclProtected,
			original.Owner,
			original.Group,
			systemAcl: null,
			DuplicateAcl(originalDacl));
		ApplyRawDacl(settingsPath, expanded);
		string expandedSddl = ReadSettingsSecuritySddl(settingsPath);

		Assert.False(
			AntigravityStatusLineIntegration
				.SettingsSecurityCanRepairLegacyExpansion(
					originalSddl,
					expandedSddl),
			DescribeSecurityComparison(originalSddl, expandedSddl));
		Assert.True(AntigravityStatusLineIntegration
			.TryValidateAndRepairCommittedSettingsSecurity(
				settingsPath,
				settingsBytes,
				originalSddl,
				authoritativeRepairPrerequisiteSddl: expandedSddl));

		string repairedSddl = ReadSettingsSecuritySddl(settingsPath);
		Assert.True(
			AntigravityStatusLineIntegration
				.SettingsSecurityMatchesAfterReplacement(
					originalSddl,
					repairedSddl),
			DescribeSecurityComparison(originalSddl, repairedSddl));
		Assert.False(AntigravityStatusLineIntegration
			.SettingsSecurityCanRepairLegacyExpansion(
				originalSddl,
				repairedSddl));
	}

	[Fact]
	public void SettingsSecurityMatch_AllowsOnlyAddedAutoInheritedFlag()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		ControlFlags legacyFlags =
			ControlFlags.SelfRelative |
			ControlFlags.DiscretionaryAclPresent;
		SecurityIdentifier owner = new(WellKnownSidType.LocalSystemSid, null);
		SecurityIdentifier group = new(
			WellKnownSidType.BuiltinAdministratorsSid,
			null);
		RawAcl inheritedDacl = CreateComparatorDacl(
			inherited: true,
			reverse: false,
			firstAccessMask: 1);
		string original = CreateSecuritySddl(
			legacyFlags,
			owner,
			group,
			inheritedDacl);
		string normalized = CreateSecuritySddl(
			legacyFlags | ControlFlags.DiscretionaryAclAutoInherited,
			owner,
			group,
			inheritedDacl);
		string expanded = CreateSecuritySddl(
			legacyFlags | ControlFlags.DiscretionaryAclAutoInherited,
			owner,
			group,
			DuplicateAcl(inheritedDacl));

		Assert.True(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(original, original));
		Assert.True(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(original, normalized));
		Assert.True(AntigravityStatusLineIntegration
			.SettingsSecurityCanRepairLegacyExpansion(original, expanded));
		Assert.False(AntigravityStatusLineIntegration
			.SettingsSecurityCanRepairLegacyExpansion(original, normalized));
	}

	[Fact]
	public void SettingsSecurityMatch_RejectsEveryOtherDescriptorChange()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		ControlFlags legacyFlags =
			ControlFlags.SelfRelative |
			ControlFlags.DiscretionaryAclPresent;
		ControlFlags normalizedFlags =
			legacyFlags |
			ControlFlags.DiscretionaryAclAutoInherited;
		SecurityIdentifier owner = new(WellKnownSidType.LocalSystemSid, null);
		SecurityIdentifier otherOwner = new(
			WellKnownSidType.LocalServiceSid,
			null);
		SecurityIdentifier group = new(
			WellKnownSidType.BuiltinAdministratorsSid,
			null);
		SecurityIdentifier otherGroup = new(
			WellKnownSidType.BuiltinUsersSid,
			null);
		RawAcl inheritedDacl = CreateComparatorDacl(
			inherited: true,
			reverse: false,
			firstAccessMask: 1);
		string original = CreateSecuritySddl(
			legacyFlags,
			owner,
			group,
			inheritedDacl);
		string originallyNormalized = CreateSecuritySddl(
			normalizedFlags,
			owner,
			group,
			inheritedDacl);
		RawAcl nonInheritedDacl = CreateComparatorDacl(
			inherited: false,
			reverse: false,
			firstAccessMask: 1);
		string nonInheritedOriginal = CreateSecuritySddl(
			legacyFlags,
			owner,
			group,
			nonInheritedDacl);
		string nonInheritedNormalized = CreateSecuritySddl(
			normalizedFlags,
			owner,
			group,
			nonInheritedDacl);

		string[] rejected =
		[
			CreateSecuritySddl(
				normalizedFlags | ControlFlags.DiscretionaryAclProtected,
				owner,
				group,
				inheritedDacl),
			CreateSecuritySddl(
				normalizedFlags |
					ControlFlags.DiscretionaryAclAutoInheritRequired,
				owner,
				group,
				inheritedDacl),
			CreateSecuritySddl(normalizedFlags, otherOwner, group, inheritedDacl),
			CreateSecuritySddl(normalizedFlags, owner, otherGroup, inheritedDacl),
			CreateSecuritySddl(
				normalizedFlags,
				owner,
				group,
				CreateComparatorDacl(
					inherited: true,
					reverse: false,
					firstAccessMask: 2)),
			CreateSecuritySddl(
				normalizedFlags,
				owner,
				group,
				CreateComparatorDacl(
					inherited: true,
					reverse: true,
					firstAccessMask: 1)),
			CreateSecuritySddl(normalizedFlags, owner, group, dacl: null)
		];

		Assert.False(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(
				originallyNormalized,
				original));

		foreach (string candidate in rejected)
		{
			Assert.False(AntigravityStatusLineIntegration
				.SettingsSecurityMatchesAfterReplacement(original, candidate));
		}

		Assert.False(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(
				nonInheritedOriginal,
				nonInheritedNormalized));

		string protectedOriginal = CreateSecuritySddl(
			legacyFlags | ControlFlags.DiscretionaryAclProtected,
			owner,
			group,
			inheritedDacl);
		string protectedNormalized = CreateSecuritySddl(
			normalizedFlags | ControlFlags.DiscretionaryAclProtected,
			owner,
			group,
			inheritedDacl);
		Assert.False(AntigravityStatusLineIntegration
			.SettingsSecurityMatchesAfterReplacement(
				protectedOriginal,
				protectedNormalized));
	}

	[Fact]
	public void EnsureInstalled_UsesPrivateHelperAndAtomicSettingsUpdate()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		string packageDirectory = Path.Combine(
			temporaryDirectory.Path,
			"package");
		string settingsDirectory = Path.Combine(
			temporaryDirectory.Path,
			"profile",
			".gemini",
			"antigravity-cli");
		string applicationDirectory = Path.Combine(
			temporaryDirectory.Path,
			"application");
		Directory.CreateDirectory(packageDirectory);
		Directory.CreateDirectory(settingsDirectory);
		Directory.CreateDirectory(applicationDirectory);
		string helperPath = Path.Combine(
			packageDirectory,
			AntigravityStatusLineIntegration.PackagedHelperFileName);
		string settingsPath = Path.Combine(settingsDirectory, "settings.json");
		string privateRoot = Path.Combine(
			applicationDirectory,
			"private",
			"antigravity-statusline");
		File.WriteAllBytes(helperPath, new byte[] { 1, 2, 3, 4 });
		File.WriteAllText(settingsPath, "{\"theme\":\"dark\"}");
		const AccessControlSections SettingsSecuritySections =
			AccessControlSections.Access |
			AccessControlSections.Owner |
			AccessControlSections.Group;
		FileInfo settingsFile = new(settingsPath);
		FileSecurity explicitSettingsSecurity =
			settingsFile.GetAccessControl(SettingsSecuritySections);
		explicitSettingsSecurity.SetAccessRuleProtection(
			isProtected: true,
			preserveInheritance: true);
		settingsFile.SetAccessControl(explicitSettingsSecurity);
		string originalSddl = settingsFile
			.GetAccessControl(SettingsSecuritySections)
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);

		AntigravityStatusLineIntegrationResult first =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot);
		AntigravityStatusLineIntegrationResult second =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot);

		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			first.Status);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.AlreadyInstalled,
			second.Status);
		Assert.True(AntigravityStatusLineIntegration
			.IsOwnedStatusLineConfigured(settingsPath, privateRoot));
		Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));
		string committedSddl = new FileInfo(settingsPath)
			.GetAccessControl(SettingsSecuritySections)
			.GetSecurityDescriptorSddlForm(SettingsSecuritySections);
		Assert.Equal(originalSddl, committedSddl);
		Assert.Empty(Directory.EnumerateFiles(
			settingsDirectory,
			".ai-usage-statusline-*"));
		using JsonDocument settings = JsonDocument.Parse(
			File.ReadAllBytes(settingsPath));
		string installedCommand = settings.RootElement
			.GetProperty("statusLine")
			.GetProperty("command")
			.GetString()!;
		Assert.DoesNotContain('"', installedCommand);
		Assert.EndsWith(
			" " + AntigravityStatusLineIntegration.OwnershipMarker,
			installedCommand,
			StringComparison.Ordinal);
	}

	[Fact]
	public void EnsureInstalled_ReusesFreshHashVerificationWithoutReopeningHelpers()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string helperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory, "測試 package", "私人-storage");
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot).Status);
		string privateHelperPath = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));

		using FileStream packageLock = new(
			helperPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None);
		using FileStream privateLock = new(
			privateHelperPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None);

		AntigravityStatusLineIntegrationResult second =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot);

		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.AlreadyInstalled,
			second.Status);
	}

	[Fact]
	public void EnsureInstalled_WhenPrivateHelperMetadataChanges_RehashesAndFailsClosed()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string helperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot).Status);
		string privateHelperPath = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));
		File.WriteAllBytes(privateHelperPath, new byte[] { 9, 8, 7, 6 });
		File.SetLastWriteTimeUtc(
			privateHelperPath,
			DateTime.UtcNow.AddMinutes(2));

		AntigravityStatusLineIntegrationResult result =
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot);

		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.HelperUnavailable,
			result.Status);
	}

	[Fact]
	public void EnsureInstalled_AfterRepeatedUpgrades_RetainsCurrentAndRollbackOnly()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string helperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot).Status);
		string firstGeneration = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));

		File.WriteAllBytes(helperPath, new byte[] { 2, 3, 4, 5, 6 });
		File.SetLastWriteTimeUtc(helperPath, DateTime.UtcNow.AddMinutes(2));
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot).Status);
		Assert.Equal(2, Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe").Count());

		File.WriteAllBytes(helperPath, new byte[] { 3, 4, 5, 6, 7, 8 });
		File.SetLastWriteTimeUtc(helperPath, DateTime.UtcNow.AddMinutes(4));
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot).Status);

		Assert.False(File.Exists(firstGeneration));
		Assert.Equal(2, Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe").Count());
	}

	[Fact]
	public void Prune_WhenSettingsNowReferenceAnotherOwnedHelper_RetainsIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string packagedHelperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				packagedHelperPath,
				settingsPath,
				privateRoot).Status);
		string previousHelper = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));
		File.WriteAllBytes(packagedHelperPath, new byte[] { 2, 3, 4, 5, 6 });
		File.SetLastWriteTimeUtc(
			packagedHelperPath,
			DateTime.UtcNow.AddMinutes(2));
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				packagedHelperPath,
				settingsPath,
				privateRoot).Status);
		string currentHelper = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"), path =>
				!string.Equals(
					path,
					previousHelper,
					StringComparison.OrdinalIgnoreCase));
		string externallyConfiguredHelper = Path.Combine(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-cccccccccccccccc.exe");
		File.WriteAllBytes(externallyConfiguredHelper, new byte[] { 9, 8, 7 });
		Assert.True(AntigravityPrivateKeyAcl.TryProtectNewFile(
			externallyConfiguredHelper));
		string externalCommand = AntigravityStatusLineIntegration
			.BuildOwnedCommand(externallyConfiguredHelper);
		File.WriteAllText(
			settingsPath,
			"{\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(externalCommand) +
			"}}");

		AntigravityStatusLineIntegration.TryPruneHelperGenerations(
			settingsPath,
			privateRoot,
			currentHelper,
			previousHelper);

		Assert.True(File.Exists(externallyConfiguredHelper));
		Assert.Equal(3, Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe").Count());
	}

	[Fact]
	public void RemoveOwnedStatusLine_RemovesConfigurationCaptureAndOwnedHelpers()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string helperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory, "package", "private-storage");
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot).Status);
		string capturePath = Path.Combine(
			privateRoot,
			AntigravityStatusLineCapture.CaptureFileName);
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new(
				"person@example.com",
				new DateTimeOffset(
					2026,
					8,
					12,
					1,
					2,
					3,
					TimeSpan.Zero))));
		string unownedLookalike = Path.Combine(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-aaaaaaaaaaaaaaaa.exe");
		File.WriteAllBytes(unownedLookalike, new byte[] { 8, 8, 8 });
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateFile(unownedLookalike));

		Assert.True(AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine(
			settingsPath,
			privateRoot,
			capturePath));

		using JsonDocument settings = JsonDocument.Parse(
			File.ReadAllBytes(settingsPath));
		Assert.False(settings.RootElement.TryGetProperty("statusLine", out _));
		Assert.Equal(
			"dark",
			settings.RootElement.GetProperty("theme").GetString());
		Assert.False(File.Exists(capturePath));
		Assert.False(File.Exists(Path.Combine(
			privateRoot,
			AntigravityStatusLineCapture.LockFileName)));
		Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));
		Assert.True(File.Exists(unownedLookalike));
	}

	[Fact]
	public void RemoveOwnedStatusLine_WhenOwnedHelperDeleteFails_CleanupOnlyRetrySucceeds()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string packagedHelperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				packagedHelperPath,
				settingsPath,
				privateRoot).Status);
		string ownedHelper = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));
		string capturePath = Path.Combine(
			privateRoot,
			AntigravityStatusLineCapture.CaptureFileName);
		FileStream? helperLock = null;

		try
		{
			Assert.False(AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine(
				settingsPath,
				privateRoot,
				capturePath,
				() => helperLock = new FileStream(
					ownedHelper,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read)));
		}
		finally
		{
			helperLock?.Dispose();
		}

		using (JsonDocument settings = JsonDocument.Parse(
			File.ReadAllBytes(settingsPath)))
		{
			Assert.False(settings.RootElement.TryGetProperty("statusLine", out _));
		}
		Assert.True(File.Exists(ownedHelper));

		Assert.True(AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine(
			settingsPath,
			privateRoot,
			capturePath));
		Assert.False(File.Exists(ownedHelper));
		Assert.False(Directory.Exists(privateRoot));
	}

	[Fact]
	public void RemoveOwnedStatusLine_WithConfiguredSafelyInheritedHelper_DeletesOnlyConfiguredHelper()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string packagedHelperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				packagedHelperPath,
				settingsPath,
				privateRoot).Status);
		string previouslyConfiguredPrivateHelper = Assert.Single(
			Directory.EnumerateFiles(
				privateRoot,
				"AiUsageDashboard.AntigravityCapture-*.exe"));
		string configuredInheritedHelper = Path.Combine(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-bbbbbbbbbbbbbbbb.exe");
		string unrelatedInheritedLookalike = Path.Combine(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-cccccccccccccccc.exe");
		File.WriteAllBytes(configuredInheritedHelper, new byte[] { 4, 3, 2, 1 });
		File.WriteAllBytes(unrelatedInheritedLookalike, new byte[] { 9, 8, 7, 6 });
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateFile(
			configuredInheritedHelper));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
			configuredInheritedHelper));
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateFile(
			unrelatedInheritedLookalike));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
			unrelatedInheritedLookalike));
		string configuredCommand = AntigravityStatusLineIntegration
			.BuildOwnedCommand(configuredInheritedHelper);
		File.WriteAllText(
			settingsPath,
			"{\"theme\":\"dark\",\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(configuredCommand) +
			"}}");
		string capturePath = Path.Combine(
			privateRoot,
			AntigravityStatusLineCapture.CaptureFileName);

		Assert.True(AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine(
			settingsPath,
			privateRoot,
			capturePath));

		using JsonDocument settings = JsonDocument.Parse(
			File.ReadAllBytes(settingsPath));
		Assert.False(settings.RootElement.TryGetProperty("statusLine", out _));
		Assert.False(File.Exists(configuredInheritedHelper));
		Assert.False(File.Exists(previouslyConfiguredPrivateHelper));
		Assert.True(File.Exists(unrelatedInheritedLookalike));
	}

	[Fact]
	public void RemoveOwnedStatusLine_WhenSettingsAlreadyMissing_DeletesContentAddressedInheritedHelperOnly()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string packagedHelperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				packagedHelperPath,
				settingsPath,
				privateRoot).Status);
		string previouslyConfiguredPrivateHelper = Assert.Single(
			Directory.EnumerateFiles(
				privateRoot,
				"AiUsageDashboard.AntigravityCapture-*.exe"));
		byte[] orphanedHelperBytes = [4, 3, 2, 1];
		string orphanedHashPrefix = Convert.ToHexString(
			SHA256.HashData(orphanedHelperBytes)).ToLowerInvariant()[..16];
		string orphanedInheritedHelper = Path.Combine(
			privateRoot,
			$"AiUsageDashboard.AntigravityCapture-{orphanedHashPrefix}.exe");
		string unrelatedInheritedLookalike = Path.Combine(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-ffffffffffffffff.exe");
		Assert.NotEqual("ffffffffffffffff", orphanedHashPrefix);
		File.WriteAllBytes(orphanedInheritedHelper, orphanedHelperBytes);
		File.WriteAllBytes(unrelatedInheritedLookalike, new byte[] { 9, 8, 7, 6 });
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateFile(
			orphanedInheritedHelper));
		Assert.True(AntigravityPrivateKeyAcl.IsPrivateOrSafelyInheritedFile(
			orphanedInheritedHelper));
		Assert.False(AntigravityPrivateKeyAcl.IsPrivateFile(
			unrelatedInheritedLookalike));
		File.WriteAllText(settingsPath, "{\"theme\":\"dark\"}");
		string capturePath = Path.Combine(
			privateRoot,
			AntigravityStatusLineCapture.CaptureFileName);

		Assert.True(AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine(
			settingsPath,
			privateRoot,
			capturePath));

		Assert.False(File.Exists(orphanedInheritedHelper));
		Assert.False(File.Exists(previouslyConfiguredPrivateHelper));
		Assert.True(File.Exists(unrelatedInheritedLookalike));
	}

	[Fact]
	public void RemoveOwnedStatusLine_WhenSettingsAreReinstalledBeforeCleanup_PreservesArtifacts()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string packagedHelperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				packagedHelperPath,
				settingsPath,
				privateRoot).Status);
		string ownedHelper = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));
		string capturePath = Path.Combine(
			privateRoot,
			AntigravityStatusLineCapture.CaptureFileName);
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("person@example.com", DateTimeOffset.UtcNow)));
		string reinstalledSettings =
			"{\"statusLine\":{\"type\":\"command\",\"command\":" +
			JsonSerializer.Serialize(
				AntigravityStatusLineIntegration.BuildOwnedCommand(ownedHelper)) +
			"}}";

		Assert.False(AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine(
			settingsPath,
			privateRoot,
			capturePath,
			() => File.WriteAllText(settingsPath, reinstalledSettings)));

		Assert.Equal(reinstalledSettings, File.ReadAllText(settingsPath));
		Assert.True(File.Exists(ownedHelper));
		Assert.True(File.Exists(capturePath));
	}

	[Fact]
	public void RemoveOwnedStatusLine_WithCustomConfiguration_DoesNotMutateOrClean()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using TemporaryDirectory temporaryDirectory = new();
		(string helperPath, string settingsPath, string privateRoot) =
			CreateInstallFixture(temporaryDirectory);
		Assert.Equal(
			AntigravityStatusLineIntegrationStatus.Installed,
			AntigravityStatusLineIntegration.TryEnsureInstalled(
				helperPath,
				settingsPath,
				privateRoot).Status);
		string ownedHelper = Assert.Single(Directory.EnumerateFiles(
			privateRoot,
			"AiUsageDashboard.AntigravityCapture-*.exe"));
		string capturePath = Path.Combine(
			privateRoot,
			AntigravityStatusLineCapture.CaptureFileName);
		Assert.True(AntigravityStatusLineCapture.TryWrite(
			capturePath,
			new("person@example.com", DateTimeOffset.UtcNow)));
		const string customSettings =
			"{\"theme\":\"dark\",\"statusLine\":{\"type\":\"command\",\"command\":\"my-tool.exe\"}}";
		File.WriteAllText(settingsPath, customSettings);

		Assert.False(AntigravityStatusLineIntegration.TryRemoveOwnedStatusLine(
			settingsPath,
			privateRoot,
			capturePath));

		Assert.Equal(customSettings, File.ReadAllText(settingsPath));
		Assert.True(File.Exists(capturePath));
		Assert.True(File.Exists(ownedHelper));
	}

	private static string ApplyLegacyInheritedDacl(string filePath)
	{
		FileInfo file = new(filePath);
		FileSecurity currentSecurity = file.GetAccessControl(OwnerGroupAndAccess);
		RawSecurityDescriptor current = new(
			currentSecurity.GetSecurityDescriptorBinaryForm(),
			0);
		RawAcl currentDacl = current.DiscretionaryAcl ??
			throw new InvalidOperationException("Test fixture must contain a DACL.");
		if ((current.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 ||
			!Enumerable.Range(0, currentDacl.Count).Any(
				index => (currentDacl[index].AceFlags & AceFlags.Inherited) != 0))
		{
			throw new InvalidOperationException(
				"Test fixture must have an unprotected inherited DACL.");
		}

		ControlFlags legacyControlFlags = current.ControlFlags & ~(
			ControlFlags.DiscretionaryAclAutoInherited |
			ControlFlags.DiscretionaryAclAutoInheritRequired);
		RawSecurityDescriptor legacy = new(
			legacyControlFlags,
			current.Owner,
			current.Group,
			systemAcl: null,
			currentDacl);
		byte[] descriptorBytes = new byte[legacy.BinaryLength];
		legacy.GetBinaryForm(descriptorBytes, 0);
		bool applied = SetFileSecurityW(
			filePath,
			DaclSecurityInformation,
			descriptorBytes);
		int error = Marshal.GetLastWin32Error();
		Assert.True(applied, $"SetFileSecurityW failed with Win32 error {error}.");

		string appliedSddl = file
			.GetAccessControl(OwnerGroupAndAccess)
			.GetSecurityDescriptorSddlForm(OwnerGroupAndAccess);
		RawSecurityDescriptor appliedDescriptor = new(appliedSddl);
		Assert.Equal(legacyControlFlags, appliedDescriptor.ControlFlags);
		Assert.Contains(
			Enumerable.Range(0, appliedDescriptor.DiscretionaryAcl!.Count),
			index => (appliedDescriptor.DiscretionaryAcl[index].AceFlags &
				AceFlags.Inherited) != 0);
		return appliedSddl;
	}

	private static void ApplyRawDacl(
		string filePath,
		RawSecurityDescriptor descriptor)
	{
		byte[] descriptorBytes = new byte[descriptor.BinaryLength];
		descriptor.GetBinaryForm(descriptorBytes, 0);
		bool applied = SetFileSecurityW(
			filePath,
			DaclSecurityInformation,
			descriptorBytes);
		int error = Marshal.GetLastWin32Error();
		Assert.True(applied, $"SetFileSecurityW failed with Win32 error {error}.");
	}

	private static string ReadSettingsSecuritySddl(string filePath)
	{
		return new FileInfo(filePath)
			.GetAccessControl(OwnerGroupAndAccess)
			.GetSecurityDescriptorSddlForm(OwnerGroupAndAccess);
	}

	private static string DescribeSecurityComparison(
		string expectedSddl,
		string actualSddl)
	{
		try
		{
			RawSecurityDescriptor expected = new(expectedSddl);
			RawSecurityDescriptor actual = new(actualSddl);
			bool ownerMatches = expected.Owner is not null &&
				actual.Owner is not null &&
				expected.Owner.Equals(actual.Owner);
			bool groupMatches = expected.Group is not null &&
				actual.Group is not null &&
				expected.Group.Equals(actual.Group);
			bool daclMatches = RawAclBytesEqual(
				expected.DiscretionaryAcl,
				actual.DiscretionaryAcl);
			bool daclMatchesIgnoringInherited = RawAclBytesEqualIgnoringInherited(
				expected.DiscretionaryAcl,
				actual.DiscretionaryAcl);
			bool daclMultisetMatchesIgnoringInherited =
				RawAclMultisetEqualIgnoringInherited(
					expected.DiscretionaryAcl,
					actual.DiscretionaryAcl);
			bool repairableLegacyExpansion =
				AntigravityStatusLineIntegration
					.SettingsSecurityCanRepairLegacyExpansion(
						expectedSddl,
						actualSddl);
			return
				$"Exact={string.Equals(expectedSddl, actualSddl, StringComparison.Ordinal)}; " +
				$"ExpectedFlags=0x{(int)expected.ControlFlags:X4}; " +
				$"ActualFlags=0x{(int)actual.ControlFlags:X4}; " +
				$"OwnerEqual={ownerMatches}; GroupEqual={groupMatches}; " +
				$"DaclBinaryEqual={daclMatches}; " +
				$"DaclIgnoringInheritedEqual={daclMatchesIgnoringInherited}; " +
				$"DaclIgnoringInheritedMultisetEqual={daclMultisetMatchesIgnoringInherited}; " +
				$"RepairableLegacyExpansion={repairableLegacyExpansion}; " +
				$"ExpectedAclRevision={FormatAclRevision(expected.DiscretionaryAcl)}; " +
				$"ActualAclRevision={FormatAclRevision(actual.DiscretionaryAcl)}; " +
				$"ExpectedAceCount={expected.DiscretionaryAcl?.Count ?? -1}; " +
				$"ActualAceCount={actual.DiscretionaryAcl?.Count ?? -1}; " +
				$"{DescribeInheritedFlagChanges(expected.DiscretionaryAcl, actual.DiscretionaryAcl)}; " +
				$"ExpectedInherited={ContainsInheritedAce(expected.DiscretionaryAcl)}; " +
				$"ActualInherited={ContainsInheritedAce(actual.DiscretionaryAcl)}";
		}
		catch (Exception ex)
		{
			return $"DescriptorParseError={ex.GetType().Name}; HResult=0x{ex.HResult:X8}";
		}
	}

	private static bool RawAclBytesEqual(RawAcl? expected, RawAcl? actual)
	{
		if (expected is null ||
			actual is null ||
			expected.BinaryLength != actual.BinaryLength)
		{
			return false;
		}

		byte[] expectedBytes = new byte[expected.BinaryLength];
		byte[] actualBytes = new byte[actual.BinaryLength];
		expected.GetBinaryForm(expectedBytes, 0);
		actual.GetBinaryForm(actualBytes, 0);
		return expectedBytes.AsSpan().SequenceEqual(actualBytes);
	}

	private static bool SecurityMatchesIgnoringAutoInheritedFlag(
		string expectedSddl,
		string actualSddl)
	{
		try
		{
			RawSecurityDescriptor expected = new(expectedSddl);
			RawSecurityDescriptor actual = new(actualSddl);
			const ControlFlags IgnoredNormalization =
				ControlFlags.DiscretionaryAclAutoInherited;
			return expected.Owner is not null &&
				actual.Owner is not null &&
				expected.Owner.Equals(actual.Owner) &&
				expected.Group is not null &&
				actual.Group is not null &&
				expected.Group.Equals(actual.Group) &&
				(expected.ControlFlags & ~IgnoredNormalization) ==
					(actual.ControlFlags & ~IgnoredNormalization) &&
				RawAclBytesEqual(
					expected.DiscretionaryAcl,
					actual.DiscretionaryAcl);
		}
		catch
		{
			return false;
		}
	}

	private static bool RawAclBytesEqualIgnoringInherited(
		RawAcl? expected,
		RawAcl? actual)
	{
		if (expected is null ||
			actual is null ||
			expected.Revision != actual.Revision ||
			expected.Count != actual.Count)
		{
			return false;
		}

		for (int index = 0; index < expected.Count; index++)
		{
			GenericAce expectedAce = expected[index];
			GenericAce actualAce = actual[index];

			byte[] expectedBytes = GetAceBytesIgnoringInherited(expectedAce);
			byte[] actualBytes = GetAceBytesIgnoringInherited(actualAce);

			if (!expectedBytes.AsSpan().SequenceEqual(actualBytes))
			{
				return false;
			}
		}

		return true;
	}

	private static bool RawAclMultisetEqualIgnoringInherited(
		RawAcl? expected,
		RawAcl? actual)
	{
		if (expected is null ||
			actual is null ||
			expected.Revision != actual.Revision ||
			expected.Count != actual.Count)
		{
			return false;
		}

		string[] expectedAces = Enumerable
			.Range(0, expected.Count)
			.Select(index => Convert.ToHexString(
				GetAceBytesIgnoringInherited(expected[index])))
			.Order(StringComparer.Ordinal)
			.ToArray();
		string[] actualAces = Enumerable
			.Range(0, actual.Count)
			.Select(index => Convert.ToHexString(
				GetAceBytesIgnoringInherited(actual[index])))
			.Order(StringComparer.Ordinal)
			.ToArray();
		return expectedAces.SequenceEqual(actualAces, StringComparer.Ordinal);
	}

	private static byte[] GetAceBytesIgnoringInherited(GenericAce ace)
	{
		byte[] bytes = new byte[ace.BinaryLength];
		ace.GetBinaryForm(bytes, 0);
		bytes[1] &= unchecked((byte)~(byte)AceFlags.Inherited);
		return bytes;
	}

	private static string DescribeInheritedFlagChanges(
		RawAcl? expected,
		RawAcl? actual)
	{
		if (expected is null || actual is null || expected.Count != actual.Count)
		{
			return "IdDirectionComparable=False; IdAdded=-1; IdRemoved=-1; " +
				"IdUnchanged=-1";
		}

		int added = 0;
		int removed = 0;
		int unchanged = 0;

		for (int index = 0; index < expected.Count; index++)
		{
			bool expectedInherited =
				(expected[index].AceFlags & AceFlags.Inherited) != 0;
			bool actualInherited =
				(actual[index].AceFlags & AceFlags.Inherited) != 0;

			if (!expectedInherited && actualInherited)
			{
				added++;
			}
			else if (expectedInherited && !actualInherited)
			{
				removed++;
			}
			else
			{
				unchanged++;
			}
		}

		bool comparable = RawAclBytesEqualIgnoringInherited(expected, actual);
		return $"IdDirectionComparable={comparable}; IdAdded={added}; " +
			$"IdRemoved={removed}; IdUnchanged={unchanged}";
	}

	private static string FormatAclRevision(RawAcl? dacl)
	{
		return dacl is null ? "none" : $"0x{dacl.Revision:X2}";
	}

	private static bool ContainsInheritedAce(RawAcl? dacl)
	{
		if (dacl is null)
		{
			return false;
		}

		for (int index = 0; index < dacl.Count; index++)
		{
			if ((dacl[index].AceFlags & AceFlags.Inherited) != 0)
			{
				return true;
			}
		}

		return false;
	}

	private static RawAcl CreateComparatorDacl(
		bool inherited,
		bool reverse,
		int firstAccessMask)
	{
		AceFlags aceFlags = inherited ? AceFlags.Inherited : AceFlags.None;
		CommonAce first = new(
			aceFlags,
			AceQualifier.AccessAllowed,
			firstAccessMask,
			new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
			isCallback: false,
			opaque: null);
		CommonAce second = new(
			aceFlags,
			AceQualifier.AccessAllowed,
			4,
			new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
			isCallback: false,
			opaque: null);
		RawAcl dacl = new(GenericAcl.AclRevision, 2);
		dacl.InsertAce(0, reverse ? second : first);
		dacl.InsertAce(1, reverse ? first : second);
		return dacl;
	}

	private static RawAcl DuplicateAcl(RawAcl source)
	{
		RawAcl duplicated = new(
			source.Revision,
			checked(source.Count * 2));
		for (int copy = 0; copy < 2; copy++)
		{
			for (int index = 0; index < source.Count; index++)
			{
				byte[] aceBytes = new byte[source[index].BinaryLength];
				source[index].GetBinaryForm(aceBytes, 0);
				duplicated.InsertAce(
					duplicated.Count,
					GenericAce.CreateFromBinaryForm(aceBytes, 0));
			}
		}

		return duplicated;
	}

	private static string CreateSecuritySddl(
		ControlFlags controlFlags,
		SecurityIdentifier owner,
		SecurityIdentifier group,
		RawAcl? dacl)
	{
		return new RawSecurityDescriptor(
			controlFlags,
			owner,
			group,
			systemAcl: null,
			dacl).GetSddlForm(OwnerGroupAndAccess);
	}

	[DllImport(
		"advapi32.dll",
		EntryPoint = "SetFileSecurityW",
		CharSet = CharSet.Unicode,
		SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetFileSecurityW(
		string fileName,
		uint securityInformation,
		byte[] securityDescriptor);

	private static (string HelperPath, string SettingsPath, string PrivateRoot)
		CreateInstallFixture(
			TemporaryDirectory temporaryDirectory,
			string packageName = "package",
			string privateName = "private")
	{
		string packageDirectory = Path.Combine(
			temporaryDirectory.Path,
			packageName);
		string settingsDirectory = Path.Combine(
			temporaryDirectory.Path,
			"profile",
			".gemini",
			"antigravity-cli");
		string privateRoot = Path.Combine(
			temporaryDirectory.Path,
			privateName,
			"antigravity-statusline");
		Directory.CreateDirectory(packageDirectory);
		Directory.CreateDirectory(settingsDirectory);
		string helperPath = Path.Combine(
			packageDirectory,
			AntigravityStatusLineIntegration.PackagedHelperFileName);
		string settingsPath = Path.Combine(settingsDirectory, "settings.json");
		File.WriteAllBytes(helperPath, new byte[] { 1, 2, 3, 4 });
		File.WriteAllText(settingsPath, "{\"theme\":\"dark\"}");
		return (helperPath, settingsPath, privateRoot);
	}
}
