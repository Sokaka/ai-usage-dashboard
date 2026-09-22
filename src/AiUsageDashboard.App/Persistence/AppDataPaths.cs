using System.IO;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.Persistence;

internal static class AppDataPaths
{
	private const string ApplicationDirectoryName = "AiUsageDashboard";
	private const string AccountCleanupPendingFileName =
		"account-cleanup-pending-v1.json";
	private const string AccountSettingsFileName = "accounts.json";
	private const string AntigravityDirectoryName = "antigravity";
	private const string AntigravityConnectionPendingFileName =
		"antigravity-connection-pending-v1.json";
	private const string AntigravityVerifiedAccountBindingFileName =
		"account-display-binding-v1.json";
	private const string ClaudeDirectoryName = "claude";
	private const string ClaudeAccountBindingFileName =
		"account-binding-v1.json";
	private const string ClaudeConfigDirectoryName = "config";
	private const string ClaudeStatusLineFallbackDisabledMarkerFileName =
		"statusline-fallback-disabled-v1";
	private const string ClaudeStatusLineCaptureFileName = "statusline-v1.json";
	private const string ClaudeUsageSafetyStateFileName =
		"usage-safety-v1.json";
	private const string CodexDirectoryName = "codex";
	private const string CodexHomeDirectoryName = "home";
	private const string CodexWorkspaceBindingFileName =
		"workspace-binding-v1.json";
	private const string CopilotDirectoryName = "copilot";
	private const string CopilotHomeDirectoryName = "home";
	private const string DashboardPreferencesFileName = "preferences.json";
	private const string GrokAccountBindingFileName =
		"account-binding-v1.json";
	private const string GrokBlankWorkspaceDirectoryName = "blank-workspace";
	private const string GrokConnectionPendingFileName =
		"grok-connection-pending-v1.json";
	private const string GrokDirectoryName = "grok";
	private const string GrokHomeDirectoryName = "home";
	private const string GrokVersionProbeDirectoryName = "version-probe";
	private const string PortableSettingsImportTransactionDirectoryName =
		"portable-settings-import-transaction-v1";
	private const string UsageSnapshotCacheFileName = "usage-snapshot-v1.json";
	private const string UpdateCheckStateFileName = "update-check-state-v1.json";

	internal static string GetAccountSettingsFilePath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			AccountSettingsFileName);
	}

	internal static string GetDashboardPreferencesFilePath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			DashboardPreferencesFileName);
	}

	internal static string GetAccountCleanupPendingFilePath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			AccountCleanupPendingFileName);
	}

	internal static string GetAntigravityOfficialPrintSafetyStateFilePath()
	{
		return AntigravityOfficialPrintSafetyStatePaths.GetDefaultFilePath();
	}

	internal static string GetAntigravityConnectionPendingFilePath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			AntigravityConnectionPendingFileName);
	}

	internal static string GetAntigravityVerifiedAccountBindingFilePath(
		Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity 帳號識別碼不可為空。",
				nameof(accountId));
		}

		return Path.Combine(
			GetApplicationDataDirectory(),
			AntigravityDirectoryName,
			accountId.ToString("N"),
			AntigravityVerifiedAccountBindingFileName);
	}

	internal static string GetGrokConnectionPendingFilePath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			GrokConnectionPendingFileName);
	}

	internal static string GetGrokAccountBindingFilePath(Guid accountId)
	{
		return Path.Combine(
			GetGrokAccountDirectory(accountId),
			GrokAccountBindingFileName);
	}

	internal static string GetPortableSettingsImportTransactionDirectoryPath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			PortableSettingsImportTransactionDirectoryName);
	}

	internal static string GetUsageSnapshotCacheFilePath(
		ProviderKind provider,
		Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"用量快取的帳號識別碼不可為空。",
				nameof(accountId));
		}

		string providerDirectoryName = provider switch
		{
			ProviderKind.Claude => ClaudeDirectoryName,
			ProviderKind.Codex => CodexDirectoryName,
			ProviderKind.Copilot => CopilotDirectoryName,
			ProviderKind.Antigravity => AntigravityDirectoryName,
			ProviderKind.Grok => GrokDirectoryName,
			_ => throw new ArgumentOutOfRangeException(
				nameof(provider),
				provider,
				"未知的用量提供者。")
		};

		return Path.Combine(
			GetApplicationDataDirectory(),
			providerDirectoryName,
			accountId.ToString("N"),
			UsageSnapshotCacheFileName);
	}

	internal static string GetClaudeConfigDirectory(Guid accountId)
	{
		return Path.Combine(
			GetClaudeAccountDirectory(accountId),
			ClaudeConfigDirectoryName);
	}

	internal static string GetUpdateCheckStateFilePath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			UpdateCheckStateFileName);
	}

	internal static string GetClaudeAccountBindingFilePath(Guid accountId)
	{
		return Path.Combine(
			GetClaudeAccountDirectory(accountId),
			ClaudeAccountBindingFileName);
	}

	internal static string GetClaudeStatusLineCaptureFilePath(Guid accountId)
	{
		return Path.Combine(
			GetClaudeAccountDirectory(accountId),
			ClaudeStatusLineCaptureFileName);
	}

	internal static string GetClaudeStatusLineFallbackDisabledMarkerFilePath(
		Guid accountId)
	{
		return Path.Combine(
			GetClaudeAccountDirectory(accountId),
			ClaudeStatusLineFallbackDisabledMarkerFileName);
	}

	internal static string GetClaudeUsageSafetyStateFilePath(Guid accountId)
	{
		return Path.Combine(
			GetClaudeAccountDirectory(accountId),
			ClaudeUsageSafetyStateFileName);
	}

	internal static string GetClaudeDataDirectoryPath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			ClaudeDirectoryName);
	}

	internal static string GetCodexHomeDirectory(Guid accountId)
	{
		return Path.Combine(
			GetCodexAccountDirectory(accountId),
			CodexHomeDirectoryName);
	}

	internal static string GetCodexWorkspaceBindingFilePath(Guid accountId)
	{
		return Path.Combine(
			GetCodexAccountDirectory(accountId),
			CodexWorkspaceBindingFileName);
	}

	internal static string GetCodexDataDirectoryPath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			CodexDirectoryName);
	}

	internal static string GetCopilotHomeDirectory(Guid accountId)
	{
		return Path.Combine(
			GetCopilotAccountDirectory(accountId),
			CopilotHomeDirectoryName);
	}

	internal static string GetGrokDataDirectoryPath()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			GrokDirectoryName);
	}

	internal static string GetGrokHomeDirectory(Guid accountId)
	{
		return Path.Combine(
			GetGrokAccountDirectory(accountId),
			GrokHomeDirectoryName);
	}

	internal static string GetGrokBlankWorkspaceDirectory(Guid accountId)
	{
		return Path.Combine(
			GetGrokAccountDirectory(accountId),
			GrokBlankWorkspaceDirectoryName);
	}

	internal static string GetGrokVersionProbeRootDirectory()
	{
		return Path.Combine(
			GetApplicationDataDirectory(),
			GrokDirectoryName,
			GrokVersionProbeDirectoryName);
	}

	private static string GetApplicationDataDirectory()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);

		if (string.IsNullOrWhiteSpace(localApplicationData))
		{
			throw new InvalidOperationException("無法取得本機應用程式資料目錄。");
		}

		return Path.Combine(
			localApplicationData,
			ApplicationDirectoryName);
	}

	private static string GetClaudeAccountDirectory(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Claude 帳號識別碼不可為空。", nameof(accountId));
		}

		return Path.Combine(
			GetApplicationDataDirectory(),
			ClaudeDirectoryName,
			accountId.ToString("N"));
	}

	private static string GetCodexAccountDirectory(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Codex 帳號識別碼不可為空。", nameof(accountId));
		}

		return Path.Combine(
			GetApplicationDataDirectory(),
			CodexDirectoryName,
			accountId.ToString("N"));
	}

	private static string GetCopilotAccountDirectory(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Copilot 帳號識別碼不可為空。",
				nameof(accountId));
		}

		return Path.Combine(
			GetApplicationDataDirectory(),
			CopilotDirectoryName,
			accountId.ToString("N"));
	}

	private static string GetGrokAccountDirectory(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException("Grok 帳號識別碼不可為空。", nameof(accountId));
		}

		return Path.Combine(
			GetApplicationDataDirectory(),
			GrokDirectoryName,
			accountId.ToString("N"));
	}
}
