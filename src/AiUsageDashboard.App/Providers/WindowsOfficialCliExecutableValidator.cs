using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.App.Providers;

internal enum OfficialCliExecutableValidationFailureReason
{
	InvalidPath,
	NotFound,
	UnsafePath,
	InvalidSignature,
	InvalidVersion,
	VersionProbeContainmentFailed,
	VersionProbeFailed
}

internal sealed class OfficialCliExecutableValidationException :
	InvalidOperationException
{
	internal OfficialCliExecutableValidationFailureReason FailureReason { get; }

	internal OfficialCliExecutableValidationException(
		OfficialCliExecutableValidationFailureReason failureReason,
		string message,
		Exception? innerException = null)
		: base(message, innerException)
	{
		FailureReason = failureReason;
	}
}

internal sealed class WindowsOfficialCliExecutableValidator
{
	private const string CommonNameOidValue = "2.5.4.3";

	private readonly string _expectedSignerCommonName;
	private readonly Func<string, bool> _hasExpectedVersion;
	private readonly Func<string, WindowsAuthenticodeInspection> _inspectSignature;
	private readonly Func<string, bool> _isCanonicalNonReparseFile;
	private readonly Func<string, bool> _isFixedDrivePath;
	private readonly Func<string, bool> _isPathAclSafe;
	private readonly Func<WindowsOfficialCliExecutableLease, IDisposable>?
		_retainExecutableLeaseDuringVersionProbe;

	internal WindowsOfficialCliExecutableValidator(
		string expectedSignerCommonName,
		Func<string, bool> hasExpectedVersion)
		: this(
			expectedSignerCommonName,
			hasExpectedVersion,
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			WindowsExecutablePathSecurity.IsPathAclSafe)
	{
	}

	internal WindowsOfficialCliExecutableValidator(
		string expectedSignerCommonName,
		Func<string, bool> hasExpectedVersion,
		Func<string, bool> isPathAclSafe)
		: this(
			expectedSignerCommonName,
			hasExpectedVersion,
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			isPathAclSafe)
	{
	}

	internal WindowsOfficialCliExecutableValidator(
		string expectedSignerCommonName,
		Func<string, bool> hasExpectedVersion,
		Func<string, bool> isPathAclSafe,
		Func<WindowsOfficialCliExecutableLease, IDisposable>
			retainExecutableLeaseDuringVersionProbe)
		: this(
			expectedSignerCommonName,
			hasExpectedVersion,
			path => new WindowsAuthenticodeInspector().Inspect(path),
			WindowsExecutablePathSecurity.IsCanonicalNonReparseFile,
			WindowsExecutablePathSecurity.IsFixedDrivePath,
			isPathAclSafe,
			retainExecutableLeaseDuringVersionProbe)
	{
	}

	internal WindowsOfficialCliExecutableValidator(
		string expectedSignerCommonName,
		Func<string, bool> hasExpectedVersion,
		Func<string, WindowsAuthenticodeInspection> inspectSignature,
		Func<string, bool> isCanonicalNonReparseFile,
		Func<string, bool> isFixedDrivePath,
		Func<string, bool> isPathAclSafe,
		Func<WindowsOfficialCliExecutableLease, IDisposable>?
			retainExecutableLeaseDuringVersionProbe = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedSignerCommonName);
		_expectedSignerCommonName = expectedSignerCommonName;
		_hasExpectedVersion = hasExpectedVersion ??
			throw new ArgumentNullException(nameof(hasExpectedVersion));
		_inspectSignature = inspectSignature ??
			throw new ArgumentNullException(nameof(inspectSignature));
		_isCanonicalNonReparseFile = isCanonicalNonReparseFile ??
			throw new ArgumentNullException(nameof(isCanonicalNonReparseFile));
		_isFixedDrivePath = isFixedDrivePath ??
			throw new ArgumentNullException(nameof(isFixedDrivePath));
		_isPathAclSafe = isPathAclSafe ??
			throw new ArgumentNullException(nameof(isPathAclSafe));
		_retainExecutableLeaseDuringVersionProbe =
			retainExecutableLeaseDuringVersionProbe;
	}

	internal string Validate(string executablePath)
	{
		return Validate(executablePath, inspectSignature: true);
	}

	internal string ValidateStaged(WindowsOfficialCliExecutableLease executableLease)
	{
		ArgumentNullException.ThrowIfNull(executableLease);
		using WindowsOfficialCliExecutableLease validationLease =
			executableLease.Duplicate();

		if (!validationLease.IsProtected)
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason.UnsafePath,
				"Official CLI executable validation requires a protected staging lease.");
		}

		using IDisposable? executableLeaseRetention =
			CreateVersionProbeExecutableLeaseRetention(validationLease);
		return Validate(validationLease.ExecutablePath, inspectSignature: false);
	}

	private IDisposable? CreateVersionProbeExecutableLeaseRetention(
		WindowsOfficialCliExecutableLease validationLease)
	{
		if (_retainExecutableLeaseDuringVersionProbe is null)
		{
			return null;
		}

		WindowsOfficialCliExecutableLease? retainedLease =
			validationLease.Duplicate();

		try
		{
			IDisposable retention =
				_retainExecutableLeaseDuringVersionProbe(retainedLease);
			retainedLease = null;
			return retention;
		}
		catch (CodexCliVersionProbeContainmentException exception)
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason
					.VersionProbeContainmentFailed,
				"Official CLI executable version-probe containment could not be confirmed.",
				exception);
		}
		finally
		{
			retainedLease?.Dispose();
		}
	}

	private string Validate(string executablePath, bool inspectSignature)
	{
		if (!ProviderProcessExecution.TryNormalizeLocalExecutablePath(
				executablePath,
				out string fullPath) ||
			!string.Equals(
				Path.GetExtension(fullPath),
				".exe",
				StringComparison.OrdinalIgnoreCase) ||
			!_isFixedDrivePath(fullPath))
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason.InvalidPath,
				"Official CLI executable path is invalid.");
		}

		if (!File.Exists(fullPath))
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason.NotFound,
				"Official CLI executable was not found.");
		}

		if (!_isCanonicalNonReparseFile(fullPath) ||
			!_isPathAclSafe(fullPath))
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason.UnsafePath,
				"Official CLI executable path or access control is unsafe.");
		}

		if (inspectSignature)
		{
			WindowsAuthenticodeInspection signature;

			try
			{
				signature = _inspectSignature(fullPath);
			}
			catch (Exception exception)
			{
				throw new OfficialCliExecutableValidationException(
					OfficialCliExecutableValidationFailureReason.InvalidSignature,
					"Official CLI executable signature could not be verified.",
					exception);
			}

			if ((signature.WinVerifyTrustStatus != 0) ||
				!HasExpectedSigner(
					signature.SignerSubject,
					_expectedSignerCommonName))
			{
				throw new OfficialCliExecutableValidationException(
					OfficialCliExecutableValidationFailureReason.InvalidSignature,
					"Official CLI executable signature is invalid.");
			}
		}

		bool hasExpectedVersion;

		try
		{
			hasExpectedVersion = _hasExpectedVersion(fullPath);
		}
		catch (CodexCliVersionProbeContainmentException exception)
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason.VersionProbeContainmentFailed,
				"Official CLI executable version-probe containment could not be confirmed.",
				exception);
		}
		catch (Exception exception)
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason.VersionProbeFailed,
				"Official CLI executable version could not be verified.",
				exception);
		}

		if (!hasExpectedVersion)
		{
			throw new OfficialCliExecutableValidationException(
				OfficialCliExecutableValidationFailureReason.InvalidVersion,
				"Official CLI executable version is invalid.");
		}

		return fullPath;
	}

	internal static bool HasExpectedSigner(
		string? signerSubject,
		string expectedCommonName)
	{
		if (string.IsNullOrWhiteSpace(signerSubject) ||
			string.IsNullOrWhiteSpace(expectedCommonName))
		{
			return false;
		}

		try
		{
			X500DistinguishedName distinguishedName = new(signerSubject);

			foreach (X500RelativeDistinguishedName relativeDistinguishedName in
				distinguishedName.EnumerateRelativeDistinguishedNames())
			{
				if (relativeDistinguishedName.HasMultipleElements)
				{
					continue;
				}

				Oid attributeType =
					relativeDistinguishedName.GetSingleElementType();
				if (!string.Equals(
						attributeType.Value,
						CommonNameOidValue,
						StringComparison.Ordinal))
				{
					continue;
				}

				string? commonName =
					relativeDistinguishedName.GetSingleElementValue();
				if (string.Equals(
						commonName,
						expectedCommonName,
						StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}
		catch (CryptographicException)
		{
			return false;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}
}
