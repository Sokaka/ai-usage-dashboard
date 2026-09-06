using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class AppDataPathsTests
{
	[Fact]
	public void GetDashboardPreferencesFilePath_ReturnsApplicationScopedPath()
	{
		string preferencesPath = AppDataPaths.GetDashboardPreferencesFilePath();

		Assert.True(Path.IsPathFullyQualified(preferencesPath));
		Assert.Equal("preferences.json", Path.GetFileName(preferencesPath));
		Assert.Equal(
			"AiUsageDashboard",
			Directory.GetParent(preferencesPath)!.Name);
	}

	[Fact]
	public void GetUsageSnapshotCacheFilePath_WhenAccountsOrProvidersDiffer_ReturnsIsolatedPaths()
	{
		Guid firstAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
		string firstClaudePath = AppDataPaths.GetUsageSnapshotCacheFilePath(
			ProviderKind.Claude,
			firstAccountId);
		string secondClaudePath = AppDataPaths.GetUsageSnapshotCacheFilePath(
			ProviderKind.Claude,
			secondAccountId);
		string firstCodexPath = AppDataPaths.GetUsageSnapshotCacheFilePath(
			ProviderKind.Codex,
			firstAccountId);
		string firstGrokPath = AppDataPaths.GetUsageSnapshotCacheFilePath(
			ProviderKind.Grok,
			firstAccountId);

		Assert.All(
			new[]
			{
				firstClaudePath,
				secondClaudePath,
				firstCodexPath,
				firstGrokPath
			},
			path =>
			{
				Assert.True(Path.IsPathFullyQualified(path));
				Assert.Equal("usage-snapshot-v1.json", Path.GetFileName(path));
			});
		Assert.NotEqual(firstClaudePath, secondClaudePath);
		Assert.NotEqual(firstClaudePath, firstCodexPath);
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstClaudePath)!.Name);
		Assert.Equal(
			"claude",
			Directory.GetParent(Directory.GetParent(firstClaudePath)!.FullName)!.Name);
		Assert.Equal(
			"codex",
			Directory.GetParent(Directory.GetParent(firstCodexPath)!.FullName)!.Name);
		Assert.Equal(
			"grok",
			Directory.GetParent(Directory.GetParent(firstGrokPath)!.FullName)!.Name);
	}

	[Fact]
	public void GetUsageSnapshotCacheFilePath_WhenArgumentsAreInvalid_Throws()
	{
		Assert.Throws<ArgumentException>(() =>
			AppDataPaths.GetUsageSnapshotCacheFilePath(
				ProviderKind.Claude,
				Guid.Empty));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			AppDataPaths.GetUsageSnapshotCacheFilePath(
				(ProviderKind)int.MaxValue,
				Guid.NewGuid()));
	}

	[Fact]
	public void GetAntigravityConnectionPendingFilePath_ReturnsApplicationScopedPath()
	{
		string pendingPath =
			AppDataPaths.GetAntigravityConnectionPendingFilePath();

		Assert.True(Path.IsPathFullyQualified(pendingPath));
		Assert.Equal(
			"antigravity-connection-pending-v1.json",
			Path.GetFileName(pendingPath));
		Assert.Equal(
			"AiUsageDashboard",
			Directory.GetParent(pendingPath)!.Name);
	}

	[Fact]
	public void GetAntigravityVerifiedAccountBindingFilePath_IsPerAccountAndRejectsEmptyId()
	{
		Guid firstAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

		string firstPath =
			AppDataPaths.GetAntigravityVerifiedAccountBindingFilePath(
				firstAccountId);
		string secondPath =
			AppDataPaths.GetAntigravityVerifiedAccountBindingFilePath(
				secondAccountId);

		Assert.True(Path.IsPathFullyQualified(firstPath));
		Assert.NotEqual(firstPath, secondPath);
		Assert.Equal("account-display-binding-v1.json", Path.GetFileName(firstPath));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstPath)!.Name);
		Assert.Equal(
			"antigravity",
			Directory.GetParent(Directory.GetParent(firstPath)!.FullName)!.Name);
		Assert.Throws<ArgumentException>(() =>
			AppDataPaths.GetAntigravityVerifiedAccountBindingFilePath(Guid.Empty));
	}

	[Fact]
	public void ClaudeAccountPaths_WhenAccountIdsDiffer_AreIsolated()
	{
		Guid firstAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
		string firstConfig = AppDataPaths.GetClaudeConfigDirectory(firstAccountId);
		string secondConfig = AppDataPaths.GetClaudeConfigDirectory(secondAccountId);
		string firstCapture = AppDataPaths.GetClaudeStatusLineCaptureFilePath(
			firstAccountId);
		string secondCapture = AppDataPaths.GetClaudeStatusLineCaptureFilePath(
			secondAccountId);
		string firstMarker =
			AppDataPaths.GetClaudeStatusLineFallbackDisabledMarkerFilePath(
				firstAccountId);
		string secondMarker =
			AppDataPaths.GetClaudeStatusLineFallbackDisabledMarkerFilePath(
				secondAccountId);
		string firstSafetyState =
			AppDataPaths.GetClaudeUsageSafetyStateFilePath(firstAccountId);
		string secondSafetyState =
			AppDataPaths.GetClaudeUsageSafetyStateFilePath(secondAccountId);
		string claudeDataDirectory =
			AppDataPaths.GetClaudeDataDirectoryPath();

		Assert.All(
			new[]
			{
				firstConfig,
				secondConfig,
				firstCapture,
				secondCapture,
				firstMarker,
				secondMarker,
				firstSafetyState,
				secondSafetyState
			},
			path => Assert.True(Path.IsPathFullyQualified(path)));
		Assert.True(Path.IsPathFullyQualified(claudeDataDirectory));
		Assert.False(string.Equals(
			firstConfig,
			secondConfig,
			StringComparison.OrdinalIgnoreCase));
		Assert.False(string.Equals(
			firstCapture,
			secondCapture,
			StringComparison.OrdinalIgnoreCase));
		Assert.False(string.Equals(
			firstMarker,
			secondMarker,
			StringComparison.OrdinalIgnoreCase));
		Assert.False(string.Equals(
			firstSafetyState,
			secondSafetyState,
			StringComparison.OrdinalIgnoreCase));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstConfig)!.Name);
		Assert.Equal(
			secondAccountId.ToString("N"),
			Directory.GetParent(secondConfig)!.Name);
		Assert.Equal(
			"statusline-fallback-disabled-v1",
			Path.GetFileName(firstMarker));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstMarker)!.Name);
		Assert.Equal("usage-safety-v1.json", Path.GetFileName(firstSafetyState));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstSafetyState)!.Name);
		Assert.Equal(
			claudeDataDirectory,
			Directory.GetParent(firstSafetyState)!.Parent!.FullName,
			ignoreCase: true);
	}

	[Fact]
	public void GetCodexHomeDirectory_WhenAccountIdsDiffer_ReturnsIsolatedHomes()
	{
		Guid firstAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

		string firstHome = AppDataPaths.GetCodexHomeDirectory(firstAccountId);
		string secondHome = AppDataPaths.GetCodexHomeDirectory(secondAccountId);

		Assert.True(Path.IsPathFullyQualified(firstHome));
		Assert.False(string.Equals(
			firstHome,
			secondHome,
			StringComparison.OrdinalIgnoreCase));
		Assert.Equal("home", Path.GetFileName(firstHome));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstHome)!.Name);
		Assert.Equal(
			secondAccountId.ToString("N"),
			Directory.GetParent(secondHome)!.Name);
	}

	[Fact]
	public void GetCopilotHomeDirectory_WhenAccountIdsDiffer_ReturnsIsolatedHomes()
	{
		Guid firstAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

		string firstHome = AppDataPaths.GetCopilotHomeDirectory(firstAccountId);
		string secondHome = AppDataPaths.GetCopilotHomeDirectory(secondAccountId);

		Assert.True(Path.IsPathFullyQualified(firstHome));
		Assert.False(string.Equals(
			firstHome,
			secondHome,
			StringComparison.OrdinalIgnoreCase));
		Assert.Equal("home", Path.GetFileName(firstHome));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstHome)!.Name);
		Assert.Equal(
			secondAccountId.ToString("N"),
			Directory.GetParent(secondHome)!.Name);
		Assert.Equal(
			"copilot",
			Directory.GetParent(firstHome)!.Parent!.Name);
		Assert.Throws<ArgumentException>(() =>
			AppDataPaths.GetCopilotHomeDirectory(Guid.Empty));
	}

	[Fact]
	public void CodexWorkspaceBindingPaths_ArePrivatePerAccountAndRejectEmptyId()
	{
		Guid firstAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

		string firstBinding =
			AppDataPaths.GetCodexWorkspaceBindingFilePath(firstAccountId);
		string secondBinding =
			AppDataPaths.GetCodexWorkspaceBindingFilePath(secondAccountId);
		string codexDataDirectory = AppDataPaths.GetCodexDataDirectoryPath();

		Assert.True(Path.IsPathFullyQualified(firstBinding));
		Assert.True(Path.IsPathFullyQualified(codexDataDirectory));
		Assert.NotEqual(firstBinding, secondBinding);
		Assert.Equal(
			"workspace-binding-v1.json",
			Path.GetFileName(firstBinding));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstBinding)!.Name);
		Assert.Equal(
			codexDataDirectory,
			Directory.GetParent(firstBinding)!.Parent!.FullName,
			ignoreCase: true);
		Assert.Throws<ArgumentException>(() =>
			AppDataPaths.GetCodexWorkspaceBindingFilePath(Guid.Empty));
	}

	[Fact]
	public void GrokAccountPaths_AreIsolatedAndRejectEmptyAccountId()
	{
		Guid firstAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		Guid secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

		string firstHome = AppDataPaths.GetGrokHomeDirectory(firstAccountId);
		string secondHome = AppDataPaths.GetGrokHomeDirectory(secondAccountId);
		string blankWorkspace =
			AppDataPaths.GetGrokBlankWorkspaceDirectory(firstAccountId);
		string binding = AppDataPaths.GetGrokAccountBindingFilePath(firstAccountId);
		string pending = AppDataPaths.GetGrokConnectionPendingFilePath();

		Assert.True(Path.IsPathFullyQualified(firstHome));
		Assert.NotEqual(firstHome, secondHome);
		Assert.Equal("home", Path.GetFileName(firstHome));
		Assert.Equal("blank-workspace", Path.GetFileName(blankWorkspace));
		Assert.Equal("account-binding-v1.json", Path.GetFileName(binding));
		Assert.Equal(
			firstAccountId.ToString("N"),
			Directory.GetParent(firstHome)!.Name);
		Assert.Equal(
			Directory.GetParent(firstHome)!.FullName,
			Directory.GetParent(blankWorkspace)!.FullName,
			ignoreCase: true);
		Assert.Equal(
			Directory.GetParent(firstHome)!.FullName,
			Directory.GetParent(binding)!.FullName,
			ignoreCase: true);
		Assert.Equal("grok-connection-pending-v1.json", Path.GetFileName(pending));
		Assert.Throws<ArgumentException>(() =>
			AppDataPaths.GetGrokHomeDirectory(Guid.Empty));
		Assert.Throws<ArgumentException>(() =>
			AppDataPaths.GetGrokBlankWorkspaceDirectory(Guid.Empty));
		Assert.Throws<ArgumentException>(() =>
			AppDataPaths.GetGrokAccountBindingFilePath(Guid.Empty));
	}
}
