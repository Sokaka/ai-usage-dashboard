using System.Windows;
using System.Windows.Threading;

using AiUsageDashboard.Antigravity.Setup;
using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal enum AntigravityAccountSetupOutcome
{
	CompletedOfficialPrint,
	CompletionUnknown,
	Cancelled,
	Failed
}

internal interface IAntigravityAccountSetupLauncher
{
	Task<AntigravityAccountSetupOutcome> RunAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken);
}

internal sealed class AntigravityAccountSetupLauncher :
	IAntigravityAccountSetupLauncher
{
	private readonly AntigravitySetupAttemptStateStore? _attemptStateStore;
	private readonly Action<string, Exception> _reportFailure;
	private readonly Func<
		Guid,
		CancellationToken,
		Task<AntigravitySetupDialogResult>> _runDialogAsync;

	internal AntigravityAccountSetupLauncher()
		: this(
			RunDialogAsync,
			AntigravitySetupAttemptStateStore.CreateDefault(),
			(operation, exception) => AppDiagnostics.TryWrite(
				operation,
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception))
	{
	}

	internal AntigravityAccountSetupLauncher(
		Func<
			Guid,
			CancellationToken,
			Task<AntigravitySetupDialogResult>> runDialogAsync,
		AntigravitySetupAttemptStateStore? attemptStateStore = null,
		Action<string, Exception>? reportFailure = null)
	{
		_runDialogAsync = runDialogAsync ??
			throw new ArgumentNullException(nameof(runDialogAsync));
		_attemptStateStore = attemptStateStore;
		_reportFailure = reportFailure ??
			((operation, exception) => AppDiagnostics.TryWrite(
				operation,
				AppDiagnostics.GetUserFacingFailureReason(exception),
				exception));
	}

	public async Task<AntigravityAccountSetupOutcome> RunAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken)
	{
		if (setupAttemptId == Guid.Empty)
		{
			throw new ArgumentException(
				"Antigravity setup attempt ID 不可為空。",
				nameof(setupAttemptId));
		}

		if (cancellationToken.IsCancellationRequested)
		{
			return AntigravityAccountSetupOutcome.Cancelled;
		}

		try
		{
			if (_attemptStateStore is not null)
			{
				AntigravitySetupProcessIdentity processIdentity =
					AntigravitySetupProcessIdentity.CaptureCurrent();
				await _attemptStateStore.BeginLaunchAsync(
					setupAttemptId,
					processIdentity,
					DateTimeOffset.UtcNow,
					CancellationToken.None);
				await _attemptStateStore.MarkActiveAsync(
					setupAttemptId,
					processIdentity,
					CancellationToken.None);
			}

			if (cancellationToken.IsCancellationRequested)
			{
				return AntigravityAccountSetupOutcome.Cancelled;
			}

			AntigravitySetupDialogResult result =
				await _runDialogAsync(setupAttemptId, cancellationToken);

			if (result.Failure is not null)
			{
				_reportFailure(
					"antigravity-setup-window",
					result.Failure);
			}

			return result.Outcome switch
			{
				AntigravitySetupDialogOutcome.CompletedOfficialPrint =>
					AntigravityAccountSetupOutcome.CompletedOfficialPrint,
				AntigravitySetupDialogOutcome.CompletionUnknown =>
					AntigravityAccountSetupOutcome.CompletionUnknown,
				AntigravitySetupDialogOutcome.Cancelled =>
					AntigravityAccountSetupOutcome.Cancelled,
				AntigravitySetupDialogOutcome.Failed =>
					AntigravityAccountSetupOutcome.Failed,
				_ => throw new ArgumentOutOfRangeException(
					nameof(result),
					result.Outcome,
					"未知的 Antigravity 連接視窗結果。")
			};
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			return AntigravityAccountSetupOutcome.Cancelled;
		}
		catch (Exception exception)
		{
			_reportFailure("antigravity-setup-session", exception);
			return AntigravityAccountSetupOutcome.Failed;
		}
	}

	internal Task<AntigravityAccountSetupOutcome> RunAsync(
		CancellationToken cancellationToken)
	{
		return RunAsync(Guid.NewGuid(), cancellationToken);
	}

	private static Task<AntigravitySetupDialogResult> RunDialogAsync(
		Guid setupAttemptId,
		CancellationToken cancellationToken)
	{
		System.Windows.Application application =
			System.Windows.Application.Current ??
			throw new InvalidOperationException(
				"AI Usage WPF application is unavailable.");
		Dispatcher dispatcher = application.Dispatcher;

		if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
		{
			throw new InvalidOperationException(
				"AI Usage is shutting down and cannot open account setup.");
		}

		if (dispatcher.CheckAccess())
		{
			return RunDialogOnDispatcherAsync(
				application,
				setupAttemptId,
				cancellationToken);
		}

		return dispatcher.InvokeAsync(
			() => RunDialogOnDispatcherAsync(
				application,
				setupAttemptId,
				cancellationToken),
			DispatcherPriority.Normal,
			cancellationToken).Task.Unwrap();
	}

	private static async Task<AntigravitySetupDialogResult>
		RunDialogOnDispatcherAsync(
			System.Windows.Application application,
			Guid setupAttemptId,
			CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		SetupWindow window = new(
			MachineSetupServiceFactory.Create(),
			setupAttemptId);
		Window? owner = application.MainWindow;

		if ((owner is not null) && owner.IsLoaded && owner.IsVisible)
		{
			window.Owner = owner;
		}

		window.Show();
		using CancellationTokenRegistration cancellationRegistration =
			cancellationToken.Register(
				static state => RequestWindowClose((SetupWindow)state!),
				window);
		return await window.Completion;
	}

	private static void RequestWindowClose(SetupWindow window)
	{
		try
		{
			_ = window.Dispatcher.BeginInvoke(
				window.Close,
				DispatcherPriority.Send);
		}
		catch (InvalidOperationException)
		{
			// Dispatcher 關閉時會一併關閉 in-process 視窗。
		}
		catch (OperationCanceledException)
		{
			// Dispatcher 關閉時會一併關閉 in-process 視窗。
		}
	}
}
