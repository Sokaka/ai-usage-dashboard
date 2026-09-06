using System.IO;
using System.Text;
using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Persistence;

namespace AiUsageDashboard.App.Providers;

internal sealed class GrokAcpUsagePoller : IGrokUsagePoller
{
	private readonly GrokAccountOperationGate _accountOperationGate;
	private readonly IGrokAcpUsageClient _client;
	private readonly GrokProcessContainmentState _containmentState;
	private readonly IGrokCliExecutableValidator _executableValidator;
	private readonly Func<Guid, string> _getHomeDirectory;
	private readonly Func<Guid, string> _getWorkingDirectory;
	private readonly Action<string, string> _prepareDirectories;

	internal GrokAcpUsagePoller(
		IGrokAcpUsageClient client,
		IGrokCliExecutableValidator? executableValidator = null,
		GrokAccountOperationGate? accountOperationGate = null,
		Func<Guid, string>? getHomeDirectory = null,
		Func<Guid, string>? getWorkingDirectory = null,
		Action<string, string>? prepareDirectories = null,
		GrokProcessContainmentState? containmentState = null)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_containmentState = containmentState ?? GrokProcessContainmentState.Shared;
		_executableValidator = executableValidator ??
			new GrokCliExecutableValidator(_containmentState);
		_accountOperationGate = accountOperationGate ?? GrokAccountOperationGate.Shared;
		_getHomeDirectory = getHomeDirectory ?? AppDataPaths.GetGrokHomeDirectory;
		_getWorkingDirectory = getWorkingDirectory ??
			AppDataPaths.GetGrokBlankWorkspaceDirectory;
		_prepareDirectories = prepareDirectories ?? PreparePrivateDirectories;
	}

	public async Task<GrokUsagePollResult> PollAsync(
		Guid accountId,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);
		return await PollCoreAsync(
			accountId,
			principalMatcher: null,
			cancellationToken);
	}

	public async Task<GrokUsagePollResult> PollBoundAsync(
		Guid accountId,
		GrokAccountBinding binding,
		CancellationToken cancellationToken = default)
	{
		ValidateAccountId(accountId);

		if ((binding is null) ||
			(binding.AccountId != accountId) ||
			(binding.PublicBindingId == Guid.Empty) ||
			!GrokAccountBinding.IsCanonicalSalt(binding.SaltBase64) ||
			!GrokAccountBinding.IsCanonicalFingerprint(
				binding.PrincipalFingerprintSha256))
		{
			throw new GrokPrincipalBindingInvalidException();
		}

		return await PollCoreAsync(
			accountId,
			principal => MatchesBoundPrincipal(binding, principal),
			cancellationToken);
	}

	private async Task<GrokUsagePollResult> PollCoreAsync(
		Guid accountId,
		Func<GrokPrincipal, bool>? principalMatcher,
		CancellationToken cancellationToken)
	{
		_containmentState.ThrowIfCompromised();

		ProviderProcessOperationTracker operationTracker = new();
		IDisposable rawOperationLease = await _accountOperationGate.EnterAsync(
			accountId,
			cancellationToken);
		using IDisposable operationLease =
			operationTracker.HoldLease(rawOperationLease);
		string homeDirectory = Path.GetFullPath(_getHomeDirectory(accountId));
		string workingDirectory = Path.GetFullPath(_getWorkingDirectory(accountId));
		_prepareDirectories(homeDirectory, workingDirectory);
		_containmentState.ThrowIfCompromised();
		using GrokValidatedExecutable executable =
			await _executableValidator.ResolveAndValidateAsync(
				cancellationToken,
				operationTracker);
		WindowsOfficialCliExecutableLease? rawExecutableLease =
			executable.TakeExecutableLease();
		using IDisposable? executableLease = rawExecutableLease is null
			? null
			: operationTracker.HoldLease(
				_containmentState.RetainExecutableLease(rawExecutableLease));
		_containmentState.ThrowIfCompromised();

		try
		{
			GrokUsagePollResult result = await _client.QueryAsync(
				new GrokAcpLaunchOptions(
					executable.ExecutablePath,
					homeDirectory,
					workingDirectory),
				cancellationToken,
				operationTracker,
				principalMatcher);
			return result with { VersionEvidence = executable.VersionEvidence };
		}
		catch (Exception exception) when (IsVersionSensitiveFailure(exception))
		{
			exception.AttachVersionEvidence(executable.VersionEvidence);
			throw;
		}
	}

	private static void ValidateAccountId(Guid accountId)
	{
		if (accountId == Guid.Empty)
		{
			throw new ArgumentException(
				"Grok 帳號識別碼不可為空。",
				nameof(accountId));
		}
	}

	private static bool MatchesBoundPrincipal(
		GrokAccountBinding binding,
		GrokPrincipal principal)
	{
		if (!GrokAccountBinding.IsValidPrincipal(
				principal.PrincipalType,
				principal.PrincipalId))
		{
			throw new GrokUsageSchemaException(
				"Grok ACP principal 格式無效。");
		}

		return binding.MatchesPrincipal(
			principal.PrincipalType,
			principal.PrincipalId);
	}

	internal static bool IsVersionSensitiveFailure(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		return (exception is DecoderFallbackException) ||
			(exception is GrokUsageSchemaException) ||
			(exception is InvalidDataException) ||
			(exception is JsonException) ||
			(exception is FormatException) ||
			(exception is GrokAcpFailureException
			{
				Category: GrokAcpFailureCategory.Compatibility or
					GrokAcpFailureCategory.Unknown
			});
	}

	internal static void PreparePrivateDirectories(
		string homeDirectory,
		string workingDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

		string fullHomeDirectory = Path.GetFullPath(homeDirectory);
		string fullWorkingDirectory = Path.GetFullPath(workingDirectory);
		string? accountDirectory = Path.GetDirectoryName(fullHomeDirectory);
		string? workingAccountDirectory = Path.GetDirectoryName(fullWorkingDirectory);
		string? providerDirectory = accountDirectory is null
			? null
			: Path.GetDirectoryName(accountDirectory);

		if ((accountDirectory is null) ||
			(providerDirectory is null) ||
			!string.Equals(
				accountDirectory,
				workingAccountDirectory,
				StringComparison.OrdinalIgnoreCase) ||
			!AntigravityPrivateKeyAcl.TryPreparePrivateStorageDirectory(
				providerDirectory,
				out _,
				out _) ||
			!AntigravityPrivateKeyAcl.TryPrepareDirectory(accountDirectory, out _) ||
			!AntigravityPrivateKeyAcl.TryPrepareDirectory(fullHomeDirectory, out _) ||
			!AntigravityPrivateKeyAcl.TryPrepareDirectory(fullWorkingDirectory, out _) ||
			!AntigravityPrivateKeyAcl.TryPrepareDirectory(
				Path.Combine(fullHomeDirectory, "tmp"),
				out _))
		{
			throw new UnauthorizedAccessException("無法建立或驗證 Grok private home。");
		}
	}
}
