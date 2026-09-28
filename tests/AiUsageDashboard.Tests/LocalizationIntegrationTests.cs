using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

using AiUsageDashboard.App;
using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.App.ViewModels;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class LocalizationIntegrationTests
{
	[Fact]
	public async Task AccountBinding_LanguageRefreshUpdatesDisplayedStatusWithoutChangingAccountState()
	{
		await RunOnStaThreadAsync(() =>
		{
			using var english = UiText.UseLanguage(AppLanguage.English);
			var profile = new AccountProfile(
				Guid.Parse("614742e0-3f69-40c9-a785-2ab0dc2f9e73"), ProviderKind.Codex, "自訂工作帳號");
			var account = new AccountUsageViewModel(profile, canManage: true);
			AccountProfile normalizedProfile = account.Profile;
			var status = new TextBlock();
			status.SetBinding(TextBlock.TextProperty, new Binding(nameof(AccountUsageViewModel.DisplayStatusText))
			{
				Source = account
			});
			string englishStatus = status.Text;
			long lifecycleRevision = account.LifecycleRevision;
			Assert.Equal("Not connected", englishStatus);

			using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
			{
				account.RefreshLocalizedPresentation();
				Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
				Assert.Equal("尚未連接", status.Text);
				Assert.Equal(lifecycleRevision, account.LifecycleRevision);
				Assert.Same(normalizedProfile, account.Profile);
				Assert.Null(account.CurrentSnapshot);
			}

			account.RefreshLocalizedPresentation();
			Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
			Assert.Equal(englishStatus, status.Text);
			Assert.Equal("自訂工作帳號", account.AccountName);
			BindingOperations.ClearBinding(status, TextBlock.TextProperty);
		});
	}

	[Fact]
	public void DefaultShellPreferences_UseEnglish()
	{
		Assert.Equal(AppLanguage.English, DashboardShellPreferences.Default.Language);
	}

	[Theory]
	[InlineData((int)AppLanguage.English)]
	[InlineData((int)AppLanguage.TraditionalChinese)]
	public void PortableWidgetSnapshot_IncludesSelectedLanguage(int language)
	{
		PortableWidgetPreferences snapshot = FloatingWidgetWindow.CreatePortableWidgetPreferencesSnapshot(
			true, false, true, FloatingWidgetCorner.BottomRight, AppTheme.ClassicBlue,
			language: (AppLanguage)language);
		Assert.Equal((AppLanguage)language, snapshot.Language);
	}

	[Fact]
	public void RestorePoint_OnlyTracksLanguageWhenTheImportedFileIncludesIt()
	{
		PortableWidgetPreferences current = CreateWidgetPreferences(AppLanguage.TraditionalChinese);
		PortableWidgetPreferences oldImport = CreateWidgetPreferences(null);
		PortableWidgetPreferences newImport = CreateWidgetPreferences(AppLanguage.English);
		Assert.Null(FloatingWidgetWindow.CreatePortableWidgetPreferencesRestorePoint(current, oldImport)?.Language);
		Assert.Equal(AppLanguage.TraditionalChinese,
			FloatingWidgetWindow.CreatePortableWidgetPreferencesRestorePoint(current, newImport)?.Language);
		Assert.Equal(AppLanguage.TraditionalChinese, current.Language);
	}

	[Fact]
	public void ImportPreview_ShowsLanguageChangeAndMissingLanguageBehavior()
	{
		using var languageScope = UiText.UseLanguage(AppLanguage.English);
		var current = CreateSnapshot(AppLanguage.TraditionalChinese);
		var imported = CreateSnapshot(AppLanguage.English);
		var oldImport = CreateSnapshot(null);
		Assert.Equal("繁體中文 → English", FloatingWidgetWindow.GetPortableLanguagePreview(current, imported));
		Assert.Equal("Keep current language", FloatingWidgetWindow.GetPortableLanguagePreview(current, oldImport));
		Assert.Contains("• Language: 繁體中文 → English",
			FloatingWidgetWindow.CreatePortableSettingsImportPreview(current, imported), StringComparison.Ordinal);
	}

	private static PortableSettingsSnapshot CreateSnapshot(AppLanguage? language)
	{
		return new PortableSettingsSnapshot([], UsageSortMode.Manual, UsageDisplayMode.Used,
			CreateWidgetPreferences(language));
	}

	private static PortableWidgetPreferences CreateWidgetPreferences(AppLanguage? language)
	{
		return new PortableWidgetPreferences(true, false, true, FloatingWidgetCorner.BottomRight,
			AppTheme.ClassicBlue, Language: language);
	}

	private static async Task RunOnStaThreadAsync(Action action)
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try
			{
				action();
				completion.SetResult();
			}
			catch (Exception exception)
			{
				completion.SetException(exception);
			}
			finally
			{
				Dispatcher.CurrentDispatcher.InvokeShutdown();
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		try
		{
			await completion.Task;
		}
		finally
		{
			thread.Join();
		}
	}
}
