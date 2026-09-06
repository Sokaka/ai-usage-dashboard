using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal sealed record GrokExecutableVersion(
	int Major,
	int Minor,
	int Patch);

internal sealed record GrokValidatedExecutable : IDisposable
{
	private WindowsOfficialCliExecutableLease? _executableLease;

	internal string ExecutablePath { get; }
	internal WindowsOfficialCliExecutableLease? ExecutableLease => _executableLease;
	internal CliVersionEvidence VersionEvidence { get; }

	internal GrokValidatedExecutable(
		string executablePath,
		CliVersionEvidence versionEvidence,
		WindowsOfficialCliExecutableLease? executableLease = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ExecutablePath = executablePath;
		VersionEvidence = versionEvidence ??
			throw new ArgumentNullException(nameof(versionEvidence));
		_executableLease = executableLease;
	}

	internal WindowsOfficialCliExecutableLease? TakeExecutableLease()
	{
		return Interlocked.Exchange(ref _executableLease, null);
	}

	public void Dispose()
	{
		Interlocked.Exchange(ref _executableLease, null)?.Dispose();
	}
}

internal interface IGrokCliExecutableValidator
{
	Task<GrokValidatedExecutable> ResolveAndValidateAsync(
		CancellationToken cancellationToken = default,
		ProviderProcessOperationTracker? operationTracker = null);

	Task<GrokValidatedExecutable> ValidateAsync(
		string executablePath,
		CancellationToken cancellationToken = default,
		ProviderProcessOperationTracker? operationTracker = null);
}

internal sealed class GrokCliExecutableValidator : IGrokCliExecutableValidator
{
	private sealed record SynchronousValidationResult(
		string FullPath,
		GrokExecutableVersion? Version,
		WindowsOfficialCliExecutableLease ExecutableLease);

	private const int MaximumConcurrentInspections = 2;
	private const string ExpectedSignerName = "X.AI LLC";
	private static readonly SemaphoreSlim InspectionGate = new(
		MaximumConcurrentInspections,
		MaximumConcurrentInspections);
	private readonly Func<string, WindowsAuthenticodeInspection> _inspectSignature;
	private readonly Func<string, bool> _isFixedDrivePath;
	private readonly Func<string, bool> _isPathAclSafe;
	private readonly Func<string> _pathResolver;
	private readonly Func<string, GrokExecutableVersion?> _readVersion;
	private readonly GrokProcessContainmentState _containmentState;
	private readonly IGrokCliExecutableStager? _stager;
	private readonly IGrokCliVersionProbe _versionProbe;

	internal GrokCliExecutableValidator()
		: this(GrokProcessContainmentState.Shared)
	{
	}

	internal GrokCliExecutableValidator(
		GrokProcessContainmentState containmentState)
		: this(
			new GrokCliVersionProbe(
				new WindowsGrokAcpProcessFactory(containmentState)),
			containmentState)
	{
	}

	internal GrokCliExecutableValidator(IGrokCliVersionProbe versionProbe)
		: this(versionProbe, GrokProcessContainmentState.Shared)
	{
	}

	internal GrokCliExecutableValidator(
		IGrokCliVersionProbe versionProbe,
		GrokProcessContainmentState containmentState)
		: this(
			ResolveDefaultExecutablePath,
			path => new WindowsAuthenticodeInspector().Inspect(path),
			ReadEmbeddedVersion,
			versionProbe,
			IsFixedDrivePath,
			GrokCliExecutableStager.IsDefaultStagedPathAclSafe,
			GrokCliExecutableStager.Shared,
			containmentState)
	{
	}

	internal GrokCliExecutableValidator(
		Func<string> pathResolver,
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, GrokExecutableVersion?> readVersion,
		IGrokCliVersionProbe versionProbe,
		Func<string, bool>? isFixedDrivePath = null,
		Func<string, bool>? isPathAclSafe = null,
		IGrokCliExecutableStager? stager = null,
		GrokProcessContainmentState? containmentState = null)
	{
		_pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
		_inspectSignature = inspectSignature ??
			throw new ArgumentNullException(nameof(inspectSignature));
		_readVersion = readVersion ?? throw new ArgumentNullException(nameof(readVersion));
		_versionProbe = versionProbe ??
			throw new ArgumentNullException(nameof(versionProbe));
		_isFixedDrivePath = isFixedDrivePath ?? IsFixedDrivePath;
		_isPathAclSafe = isPathAclSafe ?? IsPathAclSafe;
		_stager = stager;
		_containmentState = containmentState ??
			GrokProcessContainmentState.Shared;
	}

	public Task<GrokValidatedExecutable> ResolveAndValidateAsync(
		CancellationToken cancellationToken = default,
		ProviderProcessOperationTracker? operationTracker = null)
	{
		_containmentState.ThrowIfCompromised();
		return ValidateCoreAsync(
			() =>
			{
				string resolvedPath = _pathResolver();
				return _stager?.Stage(resolvedPath, cancellationToken) ??
					WindowsOfficialCliExecutableLease.CreateUnprotected(resolvedPath);
			},
			cancellationToken,
			skipSignatureInspection: _stager is not null,
			operationTracker);
	}

	public Task<GrokValidatedExecutable> ValidateAsync(
		string executablePath,
		CancellationToken cancellationToken = default,
		ProviderProcessOperationTracker? operationTracker = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		_containmentState.ThrowIfCompromised();
		return ValidateCoreAsync(
			() => WindowsOfficialCliExecutableLease.CreateUnprotected(
				executablePath),
			cancellationToken,
			skipSignatureInspection: false,
			operationTracker);
	}

	private async Task<GrokValidatedExecutable> ValidateCoreAsync(
		Func<WindowsOfficialCliExecutableLease> resolveExecutable,
		CancellationToken cancellationToken,
		bool skipSignatureInspection,
		ProviderProcessOperationTracker? operationTracker)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await InspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		SynchronousValidationResult validation;

		try
		{
			_containmentState.ThrowIfCompromised();
			validation = await Task.Run(
				() => InspectSynchronously(
					resolveExecutable,
					cancellationToken,
					skipSignatureInspection),
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			InspectionGate.Release();
		}

		try
		{
			GrokExecutableVersion? version = validation.Version;

			if (version is null)
			{
				ProviderProcessOperationTracker probeOperationTracker =
					operationTracker ?? new ProviderProcessOperationTracker();
				using IDisposable probeExecutableLease =
					probeOperationTracker.HoldLease(
						_containmentState.RetainExecutableLease(
							validation.ExecutableLease.Duplicate()));
				_containmentState.ThrowIfCompromised();
				version = await _versionProbe.ReadVersionAsync(
					validation.FullPath,
					cancellationToken,
					probeOperationTracker).ConfigureAwait(false);
			}

			return new GrokValidatedExecutable(
				validation.FullPath,
				CliVersionPolicies.AssessGrok(version),
				validation.ExecutableLease);
		}
		catch
		{
			validation.ExecutableLease.Dispose();
			throw;
		}
	}

	private SynchronousValidationResult InspectSynchronously(
		Func<WindowsOfficialCliExecutableLease> resolveExecutable,
		CancellationToken cancellationToken,
		bool skipSignatureInspection)
	{
		cancellationToken.ThrowIfCancellationRequested();
		WindowsOfficialCliExecutableLease executableLease = resolveExecutable();

		try
		{
			string executablePath = executableLease.ExecutablePath;
			ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
			string fullPath;

			try
			{
				fullPath = Path.GetFullPath(executablePath);
			}
			catch (Exception exception) when (
				(exception is ArgumentException) ||
				(exception is NotSupportedException) ||
				(exception is PathTooLongException))
			{
				throw new GrokCliUntrustedException("Grok Build CLI 路徑格式無效。");
			}

			cancellationToken.ThrowIfCancellationRequested();
			if (!Path.IsPathFullyQualified(executablePath) ||
				!string.Equals(
					Path.GetExtension(fullPath),
					".exe",
					StringComparison.OrdinalIgnoreCase) ||
				!_isFixedDrivePath(fullPath))
			{
				throw new GrokCliUntrustedException("Grok Build CLI 必須位於本機固定磁碟的絕對 EXE 路徑。");
			}

			if (!File.Exists(fullPath))
			{
				throw new GrokCliNotFoundException("找不到官方 Grok Build CLI。");
			}

			if (!skipSignatureInspection && !executableLease.IsProtected)
			{
				executableLease.Dispose();
				FileStream stream = new(
					fullPath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read,
					bufferSize: 1,
					FileOptions.RandomAccess);
				executableLease =
					WindowsOfficialCliExecutableLease.CreateProtected(
						fullPath,
						stream);
			}

			cancellationToken.ThrowIfCancellationRequested();
			if (!IsCanonicalNonReparseFile(fullPath) || !_isPathAclSafe(fullPath))
			{
				throw new GrokCliUntrustedException("Grok Build CLI 路徑、擁有者或存取權限不符合要求。");
			}

			if (skipSignatureInspection && !executableLease.IsProtected)
			{
				throw new GrokCliUntrustedException(
					"Grok Build CLI staging validation requires a protected executable lease.");
			}

			if (!skipSignatureInspection)
			{
				cancellationToken.ThrowIfCancellationRequested();
				WindowsAuthenticodeInspection signature = _inspectSignature(fullPath);
				cancellationToken.ThrowIfCancellationRequested();

				if ((signature.WinVerifyTrustStatus != 0) ||
					!HasExpectedSigner(signature.SignerSubject))
				{
					throw new GrokCliUntrustedException("Grok Build CLI 未通過 X.AI LLC 官方簽章驗證。");
				}
			}

			GrokExecutableVersion? version = _readVersion(fullPath);
			cancellationToken.ThrowIfCancellationRequested();

			return new SynchronousValidationResult(
				fullPath,
				version,
				executableLease);
		}
		catch
		{
			executableLease.Dispose();
			throw;
		}
	}

	internal static string ResolveDefaultExecutablePath()
	{
		string userProfile = Environment.GetFolderPath(
			Environment.SpecialFolder.UserProfile);

		if (string.IsNullOrWhiteSpace(userProfile) ||
			!Path.IsPathFullyQualified(userProfile))
		{
			throw new GrokCliNotFoundException("無法確認官方 Grok Build CLI 的安裝路徑。");
		}

		return Path.Combine(userProfile, ".grok", "bin", "grok.exe");
	}

	internal static GrokExecutableVersion? ReadEmbeddedVersion(
		string executablePath)
	{
		FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);

		if ((version.FileMajorPart >= 0) &&
			(version.FileMinorPart >= 0) &&
			(version.FileBuildPart >= 0) &&
			((version.FileMajorPart != 0) ||
				(version.FileMinorPart != 0) ||
				(version.FileBuildPart != 0)))
		{
			return new GrokExecutableVersion(
				version.FileMajorPart,
				version.FileMinorPart,
				version.FileBuildPart);
		}

		string? productVersion = version.ProductVersion;

		if (string.IsNullOrWhiteSpace(productVersion))
		{
			return null;
		}

		string numericPrefix = new(
			productVersion
				.TakeWhile(character => char.IsDigit(character) || (character == '.'))
				.ToArray());
		string[] parts = numericPrefix.Split(
			'.',
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		return (parts.Length >= 3) &&
			int.TryParse(parts[0], out int major) &&
			int.TryParse(parts[1], out int minor) &&
			int.TryParse(parts[2], out int patch)
				? new GrokExecutableVersion(major, minor, patch)
				: null;
	}

	internal static bool HasExpectedSigner(string? signerSubject)
	{
		return WindowsOfficialCliExecutableValidator.HasExpectedSigner(
			signerSubject,
			ExpectedSignerName);
	}

	internal static bool IsCanonicalNonReparseFile(string absolutePath)
	{
		return WindowsExecutablePathSecurity.IsCanonicalNonReparseFile(
			absolutePath);
	}

	internal static bool IsFixedDrivePath(string absolutePath)
	{
		return WindowsExecutablePathSecurity.IsFixedDrivePath(absolutePath);
	}

	internal static bool IsPathAclSafe(string absolutePath)
	{
		return WindowsExecutablePathSecurity.IsPathAclSafe(absolutePath);
	}

	internal static bool IsAccessControlSafe(FileSystemSecurity security)
	{
		return WindowsExecutablePathSecurity.IsAccessControlSafe(security);
	}
}
