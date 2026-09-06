using System.ComponentModel;
using System.Windows;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.Licensing;
using AiUsageDashboard.LegalUi;

namespace AiUsageDashboard.Antigravity.Setup;

public partial class App : System.Windows.Application
{
	private const string SingleInstanceMutexName =
		@"Local\AiUsageDashboard.Antigravity.Setup.SingleInstance";
	private static readonly Uri DefaultPaletteUri = new(
		"Themes/Palette.xaml",
		UriKind.Relative);
	private static readonly Uri HighContrastPaletteUri = new(
		"Themes/HighContrastPalette.xaml",
		UriKind.Relative);
	private Mutex? _singleInstanceMutex;
	private bool _ownsSingleInstanceMutex;

	internal bool IsDashboardManagedLaunch { get; private set; }

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		try
		{
			if (LegalCommandLine.TryHandle(e.Args, () => LegalCatalog.Load(LegalProfile.Setup),
				LegalAcceptanceStore.CreateDefault, out int legalExitCode))
			{
				Shutdown(legalExitCode);
				return;
			}

			LegalCatalog catalog = LegalCatalog.Load(LegalProfile.Setup);
			LegalAcceptanceStore acceptanceStore = LegalAcceptanceStore.CreateDefault();
			if (!LegalTermsDialog.EnsureAccepted(catalog, acceptanceStore))
			{
				Shutdown(LegalCommandLine.AcceptanceRequiredExitCode);
				return;
			}
		}
		catch (Exception exception)
		{
			MessageBox.Show($"無法讀取或保存授權條款，尚未開始連接：{exception.Message}",
				"AI Usage 授權條款", MessageBoxButton.OK, MessageBoxImage.Error);
			Shutdown(LegalCommandLine.FailureExitCode);
			return;
		}
		ShutdownMode = ShutdownMode.OnLastWindowClose;
		IsDashboardManagedLaunch = ShouldUseDashboardManagedMode(e.Args);

		if (IsDashboardManagedLaunch)
		{
			Environment.ExitCode =
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedCancelledExitCode;

			if (!TryMarkDashboardManagedAttemptActive())
			{
				MessageBox.Show(
					"AI Usage 無法記錄這次 Antigravity 連接，因此尚未變更設定。請回到 AI Usage；它會自動再試，或顯示處理方式。",
					"無法開始 Antigravity 連接",
					MessageBoxButton.OK,
					MessageBoxImage.Error);
				Shutdown(
					AntigravityMachineSetupLaunchArguments
						.DashboardManagedCompletionUnknownExitCode);
				return;
			}
		}

		try
		{
			_singleInstanceMutex = new Mutex(
				initiallyOwned: true,
				SingleInstanceMutexName,
				out _ownsSingleInstanceMutex);

			if (!_ownsSingleInstanceMutex)
			{
				MessageBox.Show(
					"Antigravity 連接視窗已開啟。請回到既有視窗完成或取消本次連接。",
					"連接 Antigravity 帳號",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
				Shutdown(IsDashboardManagedLaunch
					? AntigravityMachineSetupLaunchArguments
						.DashboardManagedFailedExitCode
					: 0);
				return;
			}
		}
		catch
		{
			MessageBox.Show(
				"無法開啟 Antigravity 連接視窗，連接尚未開始。請關閉其他 Antigravity 連接視窗後再試。",
				"無法連接 Antigravity 帳號",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
			Shutdown(IsDashboardManagedLaunch
				? AntigravityMachineSetupLaunchArguments
					.DashboardManagedFailedExitCode
				: -1);
			return;
		}

		ApplyThemePalette();
		SystemParameters.StaticPropertyChanged +=
			SystemParameters_StaticPropertyChanged;
		SetupWindow window = new();
		MainWindow = window;
		window.Show();
	}

	private static bool TryMarkDashboardManagedAttemptActive()
	{
		string? rawAttemptId = Environment.GetEnvironmentVariable(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedSetupAttemptIdEnvironmentVariable);
		if (!Guid.TryParseExact(rawAttemptId, "N", out Guid attemptId) ||
			(attemptId == Guid.Empty))
		{
			return false;
		}

		try
		{
			AntigravitySetupAttemptStateStore.CreateDefault()
				.MarkActiveAsync(
					attemptId,
					AntigravitySetupProcessIdentity.CaptureCurrent(),
					CancellationToken.None)
				.GetAwaiter()
				.GetResult();
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static bool ShouldUseDashboardManagedMode(
		IEnumerable<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		string[] copy = arguments.ToArray();
		return (copy.Length == 1) &&
			string.Equals(
				copy[0],
				AntigravityMachineSetupLaunchArguments.DashboardManaged,
				StringComparison.Ordinal);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		SystemParameters.StaticPropertyChanged -=
			SystemParameters_StaticPropertyChanged;

		if (_singleInstanceMutex is not null)
		{
			if (_ownsSingleInstanceMutex)
			{
				try
				{
					_singleInstanceMutex.ReleaseMutex();
				}
				catch (ApplicationException)
				{
				}
			}

			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
			_ownsSingleInstanceMutex = false;
		}

		base.OnExit(e);
	}

	private void ApplyThemePalette()
	{
		Uri targetPaletteUri = SystemParameters.HighContrast
			? HighContrastPaletteUri
			: DefaultPaletteUri;
		IList<ResourceDictionary> dictionaries =
			Resources.MergedDictionaries;
		int paletteIndex = -1;

		for (int index = 0; index < dictionaries.Count; index++)
		{
			string source = dictionaries[index].Source?.OriginalString ??
				string.Empty;

			if (source.EndsWith(
				"Palette.xaml",
				StringComparison.OrdinalIgnoreCase))
			{
				paletteIndex = index;
				break;
			}
		}

		ResourceDictionary palette = new()
		{
			Source = targetPaletteUri
		};

		if (paletteIndex >= 0)
		{
			dictionaries[paletteIndex] = palette;
		}
		else
		{
			dictionaries.Insert(0, palette);
		}
	}

	private void SystemParameters_StaticPropertyChanged(
		object? sender,
		PropertyChangedEventArgs e)
	{
		if (string.Equals(
			e.PropertyName,
			nameof(SystemParameters.HighContrast),
			StringComparison.Ordinal))
		{
			ApplyThemePalette();
		}
	}
}
