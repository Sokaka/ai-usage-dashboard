using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

internal sealed record AntigravityLiveSettingsFileState(
	string AbsolutePath,
	bool Exists,
	long Length,
	DateTime CreationTimeUtc,
	DateTime LastWriteTimeUtc,
	FileAttributes Attributes,
	string? Sha256);

internal sealed record AntigravityLiveR1PrivateCalibrationCaptureConsent(
	bool IsExplicitlyGranted);

internal sealed record AntigravityLiveR1PrivateCalibrationCaptureReport(
	bool IsCaptureSuccessful,
	string? CaptureContractFingerprint,
	AntigravityLiveR0Report SafetyReport);

internal sealed class AntigravityLiveR1PrivateCalibrationCaptureResult
{
	internal AntigravityLiveR1PrivateCalibrationCaptureReport Report { get; }

	[JsonIgnore]
	internal TerminalScreenSnapshot? Snapshot { get; }

	internal AntigravityLiveR1PrivateCalibrationCaptureResult(
		AntigravityLiveR1PrivateCalibrationCaptureReport report,
		TerminalScreenSnapshot? snapshot)
	{
		Report = report;
		Snapshot = snapshot;
	}
}

internal static class AntigravityLiveR1PrivateCalibrationCaptureContract
{
	internal const string FormatVersion =
		"agy-live-r1-private-calibration-capture-contract-v2";
	internal const string SettingsBaselineFormatVersion =
		"agy-live-r1-private-calibration-settings-baseline-v1";

	internal static string Compute(
		AntigravityLiveR0Profile profile,
		bool isAlternateScreen,
		string settingsBaselineFingerprint)
	{
		ArgumentNullException.ThrowIfNull(profile);

		if ((profile.ExpectedUsageStructuralFingerprint is not null) ||
			(profile.ExpectedExactUsageFingerprint is not null) ||
			!AntigravityUsageR1SchemaParser.IsFingerprint(
				settingsBaselineFingerprint))
		{
			throw new ArgumentException(
				"Private calibration capture contract inputs are invalid.",
				nameof(profile));
		}

		using IncrementalHash hash = IncrementalHash.CreateHash(
			HashAlgorithmName.SHA256);
		Append(hash, "format", FormatVersion);
		Append(hash, "profile.id", profile.Id);
		Append(
			hash,
			"executable.absolutePath",
			profile.ExecutableFingerprint.AbsolutePath);
		Append(
			hash,
			"executable.cliVersion",
			profile.ExecutableFingerprint.CliVersion);
		Append(
			hash,
			"executable.fileVersion",
			profile.ExecutableFingerprint.FileVersion);
		Append(
			hash,
			"executable.productVersion",
			profile.ExecutableFingerprint.ProductVersion);
		Append(
			hash,
			"executable.sha256",
			profile.ExecutableFingerprint.Sha256);
		Append(
			hash,
			"executable.winVerifyTrustStatus",
			profile.ExecutableFingerprint.WinVerifyTrustStatus.ToString(
				CultureInfo.InvariantCulture));
		Append(
			hash,
			"executable.signerSubject",
			profile.ExecutableFingerprint.SignerSubject);
		Append(
			hash,
			"executable.signerThumbprint",
			profile.ExecutableFingerprint.SignerThumbprint);
		Append(hash, "workingDirectory", profile.WorkingDirectory);

		KeyValuePair<string, string>[] environment = profile.Environment
			.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.ThenBy(pair => pair.Value, StringComparer.Ordinal)
			.ToArray();
		AppendCount(hash, "environment.count", environment.Length);

		foreach (KeyValuePair<string, string> pair in environment)
		{
			Append(hash, "environment.name", pair.Key);
			Append(hash, "environment.value", pair.Value);
		}

		AppendCount(hash, "viewport.columns", profile.Columns);
		AppendCount(hash, "viewport.rows", profile.Rows);
		Append(
			hash,
			"prompt.structuralFingerprint",
			profile.ExpectedPromptStructuralFingerprint);
		Append(
			hash,
			"prompt.exactFingerprint",
			profile.ExpectedExactPromptFingerprint);
		byte[] hmacKeyCommitment = SHA256.HashData(
			profile.ExactPromptFingerprintHmacKey.Span);

		try
		{
			Append(
				hash,
				"prompt.hmacKeySha256",
				Convert.ToHexString(hmacKeyCommitment));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(hmacKeyCommitment);
		}

		string[] settingsPaths = profile.NonCredentialSettingsFiles
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();
		AppendCount(hash, "settingsPaths.count", settingsPaths.Length);

		foreach (string path in settingsPaths)
		{
			Append(hash, "settingsPath", path);
		}

		Append(
			hash,
			"settingsBaselineFingerprint",
			settingsBaselineFingerprint);

		AppendCount(
			hash,
			"maximumSavedOutputBytes",
			profile.MaximumSavedOutputBytes);
		AppendTicks(hash, "promptTimeout", profile.PromptTimeout);
		AppendTicks(hash, "usageTimeout", profile.UsageTimeout);
		AppendTicks(
			hash,
			"stableScreenDuration",
			profile.StableScreenDuration);
		AppendTicks(hash, "pollInterval", profile.PollInterval);
		AppendTicks(hash, "cleanupTimeout", profile.CleanupTimeout);
		Append(
			hash,
			"observed.isAlternateScreen",
			isAlternateScreen ? "true" : "false");
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	internal static string ComputeSettingsBaselineFingerprint(
		AntigravityLiveR0Profile profile,
		IReadOnlyList<AntigravityLiveSettingsFileState> settingsStates)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(settingsStates);

		if ((profile.NonCredentialSettingsFiles is null) ||
			(profile.ExactPromptFingerprintHmacKey.Length < 32))
		{
			throw new ArgumentException(
				"Private calibration settings baseline inputs are invalid.",
				nameof(profile));
		}

		string[] expectedPaths = profile.NonCredentialSettingsFiles
			.Select(Path.GetFullPath)
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();
		AntigravityLiveSettingsFileState[] orderedStates = settingsStates
			.OrderBy(state => state.AbsolutePath, StringComparer.Ordinal)
			.ToArray();

		if ((expectedPaths.Length == 0) ||
			(expectedPaths.Length != orderedStates.Length))
		{
			throw new ArgumentException(
				"Private calibration settings baseline shape is invalid.",
				nameof(settingsStates));
		}

		for (int index = 0; index < orderedStates.Length; index++)
		{
			AntigravityLiveSettingsFileState state = orderedStates[index];

			if ((state is null) ||
				!string.Equals(
					expectedPaths[index],
					state.AbsolutePath,
					StringComparison.Ordinal) ||
				!state.Exists ||
				(state.Length < 0) ||
				((state.Attributes &
					(FileAttributes.Directory |
						FileAttributes.ReparsePoint)) != 0) ||
				!AntigravityUsageR1SchemaParser.IsFingerprint(state.Sha256))
			{
				throw new ArgumentException(
					"Private calibration settings baseline state is invalid.",
					nameof(settingsStates));
			}
		}

		byte[] hmacKey = profile.ExactPromptFingerprintHmacKey.ToArray();

		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHMAC(
				HashAlgorithmName.SHA256,
				hmacKey);
			Append(hash, "format", SettingsBaselineFormatVersion);
			AppendCount(hash, "settings.count", orderedStates.Length);

			foreach (AntigravityLiveSettingsFileState state in orderedStates)
			{
				Append(hash, "settings.path", state.AbsolutePath);
				Append(hash, "settings.exists", "true");
				AppendCount(hash, "settings.length", state.Length);
				AppendTicks(
					hash,
					"settings.creationTimeUtc",
					state.CreationTimeUtc - DateTime.UnixEpoch);
				AppendTicks(
					hash,
					"settings.lastWriteTimeUtc",
					state.LastWriteTimeUtc - DateTime.UnixEpoch);
				AppendCount(
					hash,
					"settings.attributes",
					(long)state.Attributes);
				Append(hash, "settings.sha256", state.Sha256!);
			}

			return Convert.ToHexString(hash.GetHashAndReset());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(hmacKey);
		}
	}

	private static void Append(
		IncrementalHash hash,
		string name,
		string value)
	{
		AppendUtf8(hash, name);
		AppendUtf8(hash, value);
	}

	private static void AppendCount(
		IncrementalHash hash,
		string name,
		long value)
	{
		Append(hash, name, value.ToString(CultureInfo.InvariantCulture));
	}

	private static void AppendTicks(
		IncrementalHash hash,
		string name,
		TimeSpan value)
	{
		AppendCount(hash, name, value.Ticks);
	}

	private static void AppendUtf8(IncrementalHash hash, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		Span<byte> length = stackalloc byte[sizeof(int)];
		BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
		hash.AppendData(length);

		try
		{
			hash.AppendData(bytes);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}
}
