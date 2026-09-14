using System.Buffers.Binary;
using System.Security;

using AiUsageDashboard.Updater;

namespace AiUsageDashboard.Tests;

[Trait("Category", "WindowsIntegration")]
public sealed class ManagedStartMenuShortcutTests
{
	[Fact]
	public void Operations_WhenProgramsLookupRecovers_RetryWithoutRecreatingService()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		CreateApp(installRoot);
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		string shortcutPath = Path.Combine(programsRoot, ManagedStartMenuShortcut.ShortcutFileName);
		SecurityException lookupFailure = new("Synthetic Programs lookup denied.");
		bool canResolvePrograms = false;
		int lookupCount = 0;
		ManagedStartMenuShortcut shortcut = new(() =>
		{
			lookupCount++;
			return canResolvePrograms ? programsRoot : throw lookupFailure;
		});

		Assert.Equal(0, lookupCount);
		string? registrationWarning = shortcut.EnsurePresent(installRoot);
		string? removalWarning = shortcut.RemoveIfMatches(installRoot);
		Assert.NotNull(registrationWarning);
		Assert.Contains(lookupFailure.Message, registrationWarning, StringComparison.Ordinal);
		Assert.NotNull(removalWarning);
		Assert.Contains(lookupFailure.Message, removalWarning, StringComparison.Ordinal);
		Assert.Equal(2, lookupCount);
		Assert.False(Directory.Exists(programsRoot));

		canResolvePrograms = true;
		Assert.Null(shortcut.EnsurePresent(installRoot));
		Assert.True(File.Exists(shortcutPath));
		Assert.Null(shortcut.RemoveIfMatches(installRoot));
		Assert.False(File.Exists(shortcutPath));
		Assert.Equal(4, lookupCount);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("invalid\0Programs")]
	public void Operations_WhenProgramsPathIsInvalid_ReturnWarningsAndPreservePayload(string programsRoot)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string appPath = CreateApp(installRoot);
		ManagedStartMenuShortcut shortcut = new(programsRoot);

		string? registrationWarning = shortcut.EnsurePresent(installRoot);
		string? removalWarning = shortcut.RemoveIfMatches(installRoot);

		Assert.NotNull(registrationWarning);
		Assert.Contains(ManagedStartMenuShortcut.ShortcutFileName, registrationWarning, StringComparison.Ordinal);
		Assert.NotNull(removalWarning);
		Assert.Contains(ManagedStartMenuShortcut.ShortcutFileName, removalWarning, StringComparison.Ordinal);
		Assert.Equal("synthetic app", File.ReadAllText(appPath));
	}

	[Fact]
	public void EnsurePresent_WritesShellShortcutAndReusesItWithoutChangingBytes()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "含空白的 安裝");
		string appPath = CreateApp(installRoot);
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		string shortcutPath = Path.Combine(programsRoot, ManagedStartMenuShortcut.ShortcutFileName);
		ManagedStartMenuShortcut shortcut = new(programsRoot);

		Assert.Null(shortcut.EnsurePresent(installRoot));
		ShellShortcutDefinition definition = WindowsShellShortcut.Read(shortcutPath);
		byte[] original = File.ReadAllBytes(shortcutPath);
		Assert.Equal(appPath, definition.TargetPath);
		Assert.Equal(Path.GetDirectoryName(appPath), definition.WorkingDirectory);
		Assert.Equal(appPath, definition.IconPath);
		Assert.Equal(ManagedStartMenuShortcut.ShortcutDescription, definition.Description);
		Assert.Empty(definition.Arguments);
		Assert.Equal(0, definition.IconIndex);
		Assert.Equal(1, definition.ShowCommand);
		Assert.Equal(0, definition.Hotkey);

		Assert.Null(shortcut.EnsurePresent(installRoot));
		Assert.Equal(original, File.ReadAllBytes(shortcutPath));
		Assert.Single(Directory.GetFiles(programsRoot));
	}

	[Theory]
	[InlineData("target")]
	[InlineData("arguments")]
	[InlineData("working-directory")]
	[InlineData("description")]
	[InlineData("icon")]
	[InlineData("icon-index")]
	[InlineData("show-command")]
	[InlineData("hotkey")]
	public void ConflictingShortcut_IsPreservedDuringRegistrationAndRemoval(string changedField)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		CreateApp(installRoot);
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		string shortcutPath = Path.Combine(programsRoot, ManagedStartMenuShortcut.ShortcutFileName);
		ManagedStartMenuShortcut shortcut = new(programsRoot);
		Assert.Null(shortcut.EnsurePresent(installRoot));
		ShellShortcutDefinition original = WindowsShellShortcut.Read(shortcutPath);
		ShellShortcutDefinition conflicting = changedField switch
		{
			"target" => original with { TargetPath = CreateApp(Path.Combine(temporaryDirectory.Path, "other")) },
			"arguments" => original with { Arguments = "--startup" },
			"working-directory" => original with { WorkingDirectory = temporaryDirectory.Path },
			"description" => original with { Description = "user-created" },
			"icon" => original with { IconPath = CreateApp(Path.Combine(temporaryDirectory.Path, "other")) },
			"icon-index" => original with { IconIndex = 1 },
			"show-command" => original with { ShowCommand = 3 },
			"hotkey" => original with { Hotkey = 0x0241 },
			_ => throw new ArgumentException($"Unknown test field: {changedField}")
		};
		File.Delete(shortcutPath);
		WindowsShellShortcut.WriteNew(shortcutPath, conflicting);
		byte[] conflictingBytes = File.ReadAllBytes(shortcutPath);

		string? registrationWarning = shortcut.EnsurePresent(installRoot);
		string? removalWarning = shortcut.RemoveIfMatches(installRoot);

		Assert.NotNull(registrationWarning);
		Assert.Contains(shortcutPath, registrationWarning, StringComparison.Ordinal);
		Assert.NotNull(removalWarning);
		Assert.Contains("已保留", removalWarning, StringComparison.Ordinal);
		Assert.Equal(conflictingBytes, File.ReadAllBytes(shortcutPath));
	}

	[Fact]
	public void ShortcutWithRunAsAdministratorFlag_IsPreserved()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		CreateApp(installRoot);
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		string shortcutPath = Path.Combine(programsRoot, ManagedStartMenuShortcut.ShortcutFileName);
		ManagedStartMenuShortcut shortcut = new(programsRoot);
		Assert.Null(shortcut.EnsurePresent(installRoot));
		byte[] modified = File.ReadAllBytes(shortcutPath);
		uint flags = BinaryPrimitives.ReadUInt32LittleEndian(modified.AsSpan(20));
		BinaryPrimitives.WriteUInt32LittleEndian(modified.AsSpan(20), flags | 0x00002000);
		File.WriteAllBytes(shortcutPath, modified);

		Assert.NotNull(shortcut.EnsurePresent(installRoot));
		Assert.NotNull(shortcut.RemoveIfMatches(installRoot));
		Assert.Equal(modified, File.ReadAllBytes(shortcutPath));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void NonShortcutAtReservedName_IsPreserved(bool isDirectory)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		CreateApp(installRoot);
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		Directory.CreateDirectory(programsRoot);
		string shortcutPath = Path.Combine(programsRoot, ManagedStartMenuShortcut.ShortcutFileName);
		string canaryPath = shortcutPath;

		if (isDirectory)
		{
			Directory.CreateDirectory(shortcutPath);
			canaryPath = Path.Combine(shortcutPath, "keep.txt");
		}

		File.WriteAllText(canaryPath, "keep");
		ManagedStartMenuShortcut shortcut = new(programsRoot);

		Assert.NotNull(shortcut.EnsurePresent(installRoot));
		Assert.NotNull(shortcut.RemoveIfMatches(installRoot));
		Assert.Equal("keep", File.ReadAllText(canaryPath));
	}

	[Fact]
	public void RemoveIfMatches_RemovesOwnedShortcutAfterPayloadIsAbsentAndPreservesOtherFiles()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		string appPath = CreateApp(installRoot);
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		ManagedStartMenuShortcut shortcut = new(programsRoot);
		Assert.Null(shortcut.EnsurePresent(installRoot));
		string canaryPath = Path.Combine(programsRoot, "Other application.lnk");
		File.WriteAllText(canaryPath, "keep");
		File.Delete(appPath);

		Assert.Null(shortcut.RemoveIfMatches(installRoot));
		Assert.Null(shortcut.RemoveIfMatches(installRoot));
		Assert.False(File.Exists(Path.Combine(programsRoot, ManagedStartMenuShortcut.ShortcutFileName)));
		Assert.Equal("keep", File.ReadAllText(canaryPath));
	}

	[Fact]
	public void EnsurePresent_WhenAppIsMissing_DoesNotCreateShortcut()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string programsRoot = Path.Combine(temporaryDirectory.Path, "Programs");
		ManagedStartMenuShortcut shortcut = new(programsRoot);

		Assert.NotNull(shortcut.EnsurePresent(Path.Combine(temporaryDirectory.Path, "install")));
		Assert.False(Directory.Exists(programsRoot));
	}

	[Fact]
	public async Task ProgramsDirectoryJunction_IsRejectedWithoutChangingItsTarget()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string installRoot = Path.Combine(temporaryDirectory.Path, "install");
		CreateApp(installRoot);
		string outsideRoot = Path.Combine(temporaryDirectory.Path, "outside");
		Directory.CreateDirectory(outsideRoot);
		string canaryPath = Path.Combine(outsideRoot, ManagedStartMenuShortcut.ShortcutFileName);
		File.WriteAllText(canaryPath, "keep");
		string junctionPath = Path.Combine(temporaryDirectory.Path, "Programs");
		await JunctionTestHelper.CreateAsync(junctionPath, outsideRoot);

		try
		{
			ManagedStartMenuShortcut shortcut = new(junctionPath);
			Assert.NotNull(shortcut.EnsurePresent(installRoot));
			Assert.NotNull(shortcut.RemoveIfMatches(installRoot));
			Assert.Equal("keep", File.ReadAllText(canaryPath));
			Assert.Single(Directory.GetFiles(outsideRoot));
		}
		finally
		{
			JunctionTestHelper.Delete(junctionPath);
		}
	}

	private static string CreateApp(string installRoot)
	{
		string appPath = ManagedInstallationPaths.GetInstalledAppExecutable(installRoot);
		Directory.CreateDirectory(Path.GetDirectoryName(appPath)!);
		File.WriteAllText(appPath, "synthetic app");
		return appPath;
	}
}
