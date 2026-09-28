using System.Reflection;
using System.Windows;
using System.Windows.Controls;

using AiUsageDashboard.Antigravity.Setup;
using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App;
using AiUsageDashboard.Core.Localization;
using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.Tests;

public sealed class WindowLocalizationTests
{
	private sealed class FailedSetupService : IAntigravityMachineSetupService
	{
		public int CallCount { get; private set; }

		public Task<AntigravityMachineSetupPreparationResult> PrepareAsync(
			AntigravityMachineSetupConsent consent,
			IProgress<AntigravityMachineSetupProgress>? progress,
			CancellationToken cancellationToken)
		{
			CallCount++;
			return Task.FromResult(new AntigravityMachineSetupPreparationResult(
				AntigravityMachineSetupFailureKind.UnsupportedBuild));
		}
	}

	[Fact]
	public async Task AccountEditor_LanguageRefreshPreservesInputSelectionAndProviderRestrictions()
	{
		await RunOnStaThreadAsync(() =>
		{
			using IDisposable english = UiText.UseLanguage(AppLanguage.English);
			ResourceDictionary resources = CreateResources();
			AccountEditorWindow window = new(canAddAntigravity: false, windowResources: resources);
			try
			{
				ComboBox service = (ComboBox)window.FindName("ProviderComboBox");
				TextBox nickname = (TextBox)window.FindName("DisplayNameTextBox");
				CheckBox enabled = (CheckBox)window.FindName("EnabledCheckBox");
				service.SelectedValue = ProviderKind.Codex;
				nickname.Text = "Unfinished 自訂 nickname";
				enabled.IsChecked = false;
				Assert.Equal("Add account", ((TextBlock)window.FindName("TitleTextBlock")).Text);

				using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
				{
					ApplyStrings(resources);
					window.RefreshLocalizedPresentation();
					Assert.Equal("新增帳號", ((TextBlock)window.FindName("TitleTextBlock")).Text);
					Assert.Equal(ProviderKind.Codex, service.SelectedValue);
					Assert.Equal("Unfinished 自訂 nickname", nickname.Text);
					Assert.False(enabled.IsChecked);
					AccountEditorWindow.ProviderOption unavailable = service.Items
						.Cast<AccountEditorWindow.ProviderOption>()
						.Single(option => option.Provider == ProviderKind.Antigravity);
					Assert.False(unavailable.IsEnabled);
					Assert.Contains("不能再新增", unavailable.UnavailableReason);
					Assert.Null(window.EditedProfile);
					Assert.False(window.ConnectAfterSave);
				}

				ApplyStrings(resources);
				window.RefreshLocalizedPresentation();
				Assert.Equal("Add account", ((TextBlock)window.FindName("TitleTextBlock")).Text);
				Assert.Equal("Unfinished 自訂 nickname", nickname.Text);
				Assert.Equal(ProviderKind.Codex, service.SelectedValue);
			}
			finally
			{
				window.Close();
			}
		});
	}

	[Fact]
	public async Task WorkspacePrompt_LanguageRefreshPreservesPendingIdAndConnectionDefaults()
	{
		await RunOnStaThreadAsync(() =>
		{
			using IDisposable english = UiText.UseLanguage(AppLanguage.English);
			ResourceDictionary resources = CreateResources();
			CodexWorkspacePromptWindow window = new(true, resources);
			try
			{
				TextBox input = (TextBox)window.FindName("WorkspaceIdTextBox");
				Button standard = (Button)window.FindName("DefaultConnectionButton");
				Button workspace = (Button)window.FindName("WorkspaceConnectionButton");
				input.Text = " 11111111-1111-1111-1111-111111111111 ";
				Assert.Equal("Switch to standard account connection", standard.Content);
				using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
				{
					ApplyStrings(resources);
					window.RefreshLocalizedPresentation();
					Assert.Equal("改為一般帳號連接", standard.Content);
					Assert.Equal(" 11111111-1111-1111-1111-111111111111 ", input.Text);
					Assert.False(standard.IsDefault);
					Assert.True(workspace.IsDefault);
					Assert.Null(window.WorkspaceId);
				}
			}
			finally
			{
				window.Close();
			}
		});
	}

	[Fact]
	public async Task Setup_LanguageRefreshPreservesFailureDisclosureAndDoesNotRepeatPreparation()
	{
		await RunOnStaThreadAsync(() =>
		{
			using IDisposable english = UiText.UseLanguage(AppLanguage.English);
			ResourceDictionary resources = CreateResources();
			FailedSetupService service = new();
			SetupWindow window = new(service, Guid.NewGuid(), resources);
			try
			{
				Task preparation = (Task)typeof(SetupWindow).GetMethod(
					"StartPreparationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
					.Invoke(window, null)!;
				preparation.GetAwaiter().GetResult();
				Button disclosure = (Button)window.FindName("FailureDiagnosticDisclosureButton");
				disclosure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
				StackPanel details = (StackPanel)window.FindName("FailureDiagnosticDetailsPanel");
				Assert.Equal(Visibility.Visible, details.Visibility);
				Assert.Contains("Supported Antigravity CLI not found",
					((TextBlock)window.FindName("FailureKindTextBlock")).Text);
				using (UiText.UseLanguage(AppLanguage.TraditionalChinese))
				{
					ApplyStrings(resources);
					window.RefreshLocalizedPresentation();
					Assert.Contains("找不到支援的 Antigravity CLI",
						((TextBlock)window.FindName("FailureKindTextBlock")).Text);
					Assert.Equal(Visibility.Visible, details.Visibility);
					Assert.Equal("隱藏技術資訊", disclosure.Content);
					Assert.Equal(1, service.CallCount);
					Assert.False(window.Completion.IsCompleted);
				}
			}
			finally
			{
				AntigravitySetupWorkflow workflow = (AntigravitySetupWorkflow)typeof(SetupWindow)
					.GetField("_workflow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
				workflow.DisposeAsync().AsTask().GetAwaiter().GetResult();
				typeof(SetupWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
					.SetValue(window, true);
				window.Close();
			}
		});
	}

	private static ResourceDictionary CreateResources()
	{
		ResourceDictionary resources = new();
		resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri(
			"/AiUsageDashboard.App;component/Themes/Palette.xaml", UriKind.Relative)));
		resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri(
			"/AiUsageDashboard.App;component/Themes/Controls.xaml", UriKind.Relative)));
		resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
		ApplyStrings(resources);
		return resources;
	}

	private static void ApplyStrings(ResourceDictionary resources)
	{
		foreach ((string key, string text) in UiText.GetResources())
		{
			resources[key] = text;
		}
	}

	private static Task RunOnStaThreadAsync(Action operation)
	{
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new(() =>
		{
			try
			{
				operation();
				completion.SetResult();
			}
			catch (Exception exception)
			{
				completion.SetException(exception);
			}
		}) { IsBackground = true };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}
}
