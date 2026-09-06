using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.App.Persistence;
using AiUsageDashboard.Core.Models;
using AiUsageDashboard.Core.Providers;

namespace AiUsageDashboard.App.Providers;

internal sealed class GrokUsageProvider : IUsageProvider
{
	private const int MaximumDisplayIdentityLength = 320;
	private const string ConnectAccountMessage =
		"尚未連接 Grok 帳號。";
	private const string InstallOrUpdateMessage =
		"找不到可用且受支援的 Grok Build CLI；請安裝、更新或修復 Grok Build CLI 後再試。";
	private const string RepairConnectionMessage =
		"Grok 帳號的連接資料已損壞，請重新連接。";
	private const string RestartApplicationMessage =
		"無法確認 Grok Build CLI 是否已完全結束，因此已停止檢查用量。請重新啟動 AI Usage。";
	private const string RetryMessage =
		"暫時無法讀取 Grok 用量，稍後會自動再試。";
	private const string SwitchAccountMessage =
		"Grok Build CLI 目前登入的帳號與這裡連接的帳號不同。請切換 Grok 帳號或重新連接。";
	private const string UnsupportedBillingMessage =
		"已確認這張卡片的 Grok 帳號，但目前訂閱沒有提供可驗證的週用量；AI Usage 不會猜測用量。";
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
	private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
	private readonly IGrokAccountBindingStore _bindingStore;
	private readonly GrokBindingCommitGate _bindingCommitGate;
	private readonly IGrokUsagePoller _poller;
	private readonly TimeProvider _timeProvider;

	public ProviderKind Provider => ProviderKind.Grok;

	public TimeSpan MinimumRefreshInterval => RefreshInterval;

	internal GrokUsageProvider(
		IGrokUsagePoller poller,
		IGrokAccountBindingStore bindingStore,
		TimeProvider? timeProvider = null,
		GrokBindingCommitGate? bindingCommitGate = null)
	{
		_poller = poller ?? throw new ArgumentNullException(nameof(poller));
		_bindingStore = bindingStore ??
			throw new ArgumentNullException(nameof(bindingStore));
		_timeProvider = timeProvider ?? TimeProvider.System;
		_bindingCommitGate = bindingCommitGate ?? new GrokBindingCommitGate();
	}

	public async Task<UsageSnapshot> GetUsageAsync(
		AccountProfile account,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(account);

		if (account.Provider != ProviderKind.Grok)
		{
			throw new ArgumentException(
				"Grok provider 只能讀取 Grok 帳號。",
				nameof(account));
		}

		CliVersionEvidence? versionEvidence = null;
		string? publicBindingId = null;

		try
		{
			using IDisposable bindingLease =
				await _bindingCommitGate.EnterAsync(cancellationToken);
			GrokAccountBinding? binding;

			try
			{
				binding = await _bindingStore.LoadAsync(
					account.Id,
					cancellationToken);
			}
			catch (InvalidDataException)
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.NotConfigured,
					_timeProvider.GetUtcNow(),
					RepairConnectionMessage,
					UsageRecoveryAction.SwitchAccount);
			}

			if (binding is null)
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.NotConfigured,
					_timeProvider.GetUtcNow(),
					ConnectAccountMessage,
					UsageRecoveryAction.ConnectAccount);
			}

			if (!IsValidBinding(binding, account.Id))
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.NotConfigured,
					_timeProvider.GetUtcNow(),
					RepairConnectionMessage,
					UsageRecoveryAction.SwitchAccount);
			}

			publicBindingId =
				GrokAccountBinding.CreatePublicBindingIdentity(
					binding.PublicBindingId);

			if (!string.Equals(
					account.ProviderAccountIdentity,
					publicBindingId,
					StringComparison.Ordinal))
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.NotConfigured,
					_timeProvider.GetUtcNow(),
					SwitchAccountMessage,
					UsageRecoveryAction.SwitchAccount);
			}

			GrokUsagePollResult result = await _poller.PollBoundAsync(
				account.Id,
				binding,
				cancellationToken);
			versionEvidence = result.VersionEvidence;
			DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

			if ((result.UsageAvailability ==
					GrokUsageAvailability.AuthenticationRequired) &&
				(result.CurrentAuthUsability ==
					GrokCurrentAuthUsability.Unusable))
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.NotConfigured,
					fetchedAt,
					ConnectAccountMessage,
					UsageRecoveryAction.ConnectAccount,
					publicBindingId);
			}

			if (!result.CanCommitBinding)
			{
				return CreateBoundTransientSnapshot(
					account,
					fetchedAt,
					publicBindingId,
					AppendVersionDiagnostic(RetryMessage, versionEvidence));
			}

			await ValidatePrincipalOwnerAsync(
				account.Id,
				result.Principal,
				cancellationToken);

			string? verifiedEmail = NormalizeDisplayIdentity(
				result.Principal?.Email);

			if (result.UsageAvailability == GrokUsageAvailability.Unsupported)
			{
				return CreateEmptySnapshot(
					account,
					SnapshotStatus.Error,
					fetchedAt,
					UnsupportedBillingMessage,
					UsageRecoveryAction.Retry,
					publicBindingId,
					verifiedEmail,
					SubscriptionVerificationState.UsageUnavailable);
			}

			if (result.UsageAvailability ==
				GrokUsageAvailability.TransientProbeError)
			{
				return CreateBoundTransientSnapshot(
					account,
					fetchedAt,
					publicBindingId,
					AppendVersionDiagnostic(RetryMessage, versionEvidence),
					verifiedEmail);
			}

			if (result.UsageAvailability != GrokUsageAvailability.Available)
			{
				return CreateBoundTransientSnapshot(
					account,
					fetchedAt,
					publicBindingId,
					AppendVersionDiagnostic(RetryMessage, versionEvidence));
			}

			if (!TryCreateWeeklyMetric(
					result.WeeklyUsage,
					fetchedAt,
					out UsageMetric? metric) ||
				(metric is null))
			{
				return CreateBoundTransientSnapshot(
					account,
					fetchedAt,
					publicBindingId,
					AppendVersionDiagnostic(RetryMessage, versionEvidence),
					verifiedEmail);
			}

			return new UsageSnapshot(
				account,
				new[] { metric },
				SourceTrust.OfficialExperimental,
				SnapshotStatus.Ready,
				fetchedAt,
				result.ObservedAt,
				fetchedAt + StaleAfter,
				ProviderAccountIdentity: publicBindingId,
				ProviderAccountDisplayIdentity: verifiedEmail,
				SubscriptionVerificationState:
					SubscriptionVerificationState.Verified);
		}
		catch (OperationCanceledException) when (
			cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (GrokCliNotFoundException)
		{
			return CreateInstallOrUpdateSnapshot(account);
		}
		catch (GrokCliUntrustedException)
		{
			return CreateInstallOrUpdateSnapshot(account);
		}
		catch (GrokProcessContainmentException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				_timeProvider.GetUtcNow(),
				RestartApplicationMessage,
				UsageRecoveryAction.RestartApplication);
		}
		catch (GrokAcpFailureException exception)
		{
			return CreateAcpFailureSnapshot(
				account,
				exception,
				publicBindingId);
		}
		catch (GrokUsageNotConfiguredException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				_timeProvider.GetUtcNow(),
				ConnectAccountMessage,
				UsageRecoveryAction.ConnectAccount,
				publicBindingId);
		}
		catch (GrokUnsupportedBillingException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				_timeProvider.GetUtcNow(),
				UnsupportedBillingMessage,
				UsageRecoveryAction.Retry,
				publicBindingId,
				subscriptionVerificationState:
					SubscriptionVerificationState.UsageUnavailable);
		}
		catch (GrokPrincipalMismatchException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				_timeProvider.GetUtcNow(),
				SwitchAccountMessage,
				UsageRecoveryAction.SwitchAccount,
				publicBindingId,
				subscriptionVerificationState:
					SubscriptionVerificationState.DefiniteScopeMismatch);
		}
		catch (GrokPrincipalBindingInvalidException)
		{
			return CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				_timeProvider.GetUtcNow(),
				RepairConnectionMessage,
				UsageRecoveryAction.SwitchAccount);
		}
		catch (Exception exception) when (IsRetryableFailure(exception))
		{
			return CreateBoundTransientSnapshot(
				account,
				_timeProvider.GetUtcNow(),
				publicBindingId,
				AppendVersionDiagnostic(
					RetryMessage,
					versionEvidence,
					exception));
		}
	}

	private UsageSnapshot CreateAcpFailureSnapshot(
		AccountProfile account,
		GrokAcpFailureException exception,
		string? publicBindingId)
	{
		ArgumentNullException.ThrowIfNull(exception);
		DateTimeOffset fetchedAt = _timeProvider.GetUtcNow();

		return exception.Category switch
		{
			GrokAcpFailureCategory.Authentication => CreateEmptySnapshot(
				account,
				SnapshotStatus.NotConfigured,
				fetchedAt,
				ConnectAccountMessage,
				UsageRecoveryAction.ConnectAccount,
				publicBindingId),
			GrokAcpFailureCategory.Compatibility => CreateEmptySnapshot(
				account,
				SnapshotStatus.Error,
				fetchedAt,
				exception.AppendVersionDiagnostic(InstallOrUpdateMessage),
				UsageRecoveryAction.InstallOrUpdate),
			GrokAcpFailureCategory.Transient => CreateBoundTransientSnapshot(
				account,
				fetchedAt,
				publicBindingId,
				RetryMessage),
			_ => CreateBoundTransientSnapshot(
				account,
				fetchedAt,
				publicBindingId,
				exception.AppendVersionDiagnostic(RetryMessage))
		};
	}

	private static string AppendVersionDiagnostic(
		string message,
		CliVersionEvidence? versionEvidence,
		Exception? exception = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);

		if ((exception is not null) &&
			!GrokAcpUsagePoller.IsVersionSensitiveFailure(exception))
		{
			return message;
		}

		if (versionEvidence is not null)
		{
			return versionEvidence.AppendFailureDiagnostic(message);
		}

		return exception is null
			? message
			: exception.AppendVersionDiagnostic(message);
	}

	private UsageSnapshot CreateInstallOrUpdateSnapshot(AccountProfile account)
	{
		return CreateEmptySnapshot(
			account,
			SnapshotStatus.Error,
			_timeProvider.GetUtcNow(),
			InstallOrUpdateMessage,
			UsageRecoveryAction.InstallOrUpdate);
	}

	private static UsageSnapshot CreateEmptySnapshot(
		AccountProfile account,
		SnapshotStatus status,
		DateTimeOffset fetchedAt,
		string error,
		UsageRecoveryAction recoveryAction,
		string? providerAccountIdentity = null,
		string? providerAccountDisplayIdentity = null,
		SubscriptionVerificationState subscriptionVerificationState =
			SubscriptionVerificationState.Unverified)
	{
		return new UsageSnapshot(
			account,
			Array.Empty<UsageMetric>(),
			SourceTrust.Unavailable,
			status,
			fetchedAt,
			Error: error,
			ProviderAccountIdentity: providerAccountIdentity,
			RecoveryAction: recoveryAction,
			ProviderAccountDisplayIdentity: providerAccountDisplayIdentity,
			SubscriptionVerificationState: subscriptionVerificationState);
	}

	private static UsageSnapshot CreateBoundTransientSnapshot(
		AccountProfile account,
		DateTimeOffset fetchedAt,
		string? publicBindingId,
		string error,
		string? verifiedEmail = null)
	{
		return CreateEmptySnapshot(
			account,
			SnapshotStatus.Error,
			fetchedAt,
			error,
			UsageRecoveryAction.Retry,
			publicBindingId,
			verifiedEmail,
			SubscriptionVerificationState.TransientProbeError);
	}

	private static bool IsRetryableFailure(Exception exception)
	{
		return (exception is DecoderFallbackException) ||
			(exception is InvalidDataException) ||
			(exception is IOException) ||
			(exception is JsonException) ||
			(exception is OperationCanceledException) ||
			(exception is TimeoutException) ||
			(exception is UnauthorizedAccessException) ||
			(exception is Win32Exception) ||
			(exception is CryptographicException) ||
			(exception is FormatException);
	}

	private static bool IsValidBinding(
		GrokAccountBinding binding,
		Guid accountId)
	{
		return (binding.AccountId == accountId) &&
			(binding.PublicBindingId != Guid.Empty) &&
			GrokAccountBinding.IsCanonicalSalt(binding.SaltBase64) &&
			GrokAccountBinding.IsCanonicalFingerprint(
				binding.PrincipalFingerprintSha256);
	}

	private static string? NormalizeDisplayIdentity(string? identity)
	{
		if (string.IsNullOrWhiteSpace(identity))
		{
			return null;
		}

		string normalized = identity.Trim();
		return (normalized.Length > MaximumDisplayIdentityLength) ||
			normalized.Any(char.IsControl)
			? null
			: normalized;
	}

	private static bool TryCreateWeeklyMetric(
		GrokWeeklyUsage? weeklyUsage,
		DateTimeOffset fetchedAt,
		out UsageMetric? metric)
	{
		metric = null;

		if ((weeklyUsage is null) ||
			(weeklyUsage.ResetsAt <= fetchedAt) ||
			((weeklyUsage.UsedPercent is double usedPercent) &&
				(!double.IsFinite(usedPercent) ||
				 (usedPercent < 0) ||
				 (usedPercent > 100))))
		{
			return false;
		}

		string displayValue = weeklyUsage.UsedPercent is double percent
			? $"已使用 {percent.ToString("0.##", CultureInfo.InvariantCulture)}%"
			: "尚未提供用量";
		metric = new UsageMetric(
			"grok.weekly",
			"週用量",
			weeklyUsage.UsedPercent,
			displayValue,
			weeklyUsage.ResetsAt);
		return true;
	}

	private async Task ValidatePrincipalOwnerAsync(
		Guid accountId,
		GrokPrincipal principal,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<GrokAccountBinding> bindings =
			await _bindingStore.LoadAllAsync(cancellationToken);

		if (bindings.Any(binding =>
				(binding.AccountId != accountId) &&
				binding.MatchesPrincipal(
					principal.PrincipalType,
					principal.PrincipalId)))
		{
			throw new GrokPrincipalMismatchException();
		}
	}
}
