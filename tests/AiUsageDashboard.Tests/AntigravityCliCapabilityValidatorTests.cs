using System.IO;
using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityCliCapabilityValidatorTests
{
	private sealed class FakeExecutableInspector : IAntigravityExecutableInspector
	{
		private readonly List<string> _calls;
		private readonly Exception? _inspectionException;
		private readonly AntigravityExecutableInspection _inspection;
		private readonly Queue<AntigravityExecutableFileState> _states;

		internal int CaptureCount { get; private set; }

		internal int InspectionCount { get; private set; }

		internal FakeExecutableInspector(
			IEnumerable<AntigravityExecutableFileState> states,
			AntigravityExecutableInspection inspection,
			List<string>? calls = null,
			Exception? inspectionException = null)
		{
			_states = new Queue<AntigravityExecutableFileState>(states);
			_inspection = inspection;
			_calls = calls ?? new List<string>();
			_inspectionException = inspectionException;
		}

		public AntigravityExecutableFileState CaptureFileState(string absolutePath)
		{
			CaptureCount++;
			_calls.Add("capture");

			if (_states.Count == 0)
			{
				throw new InvalidOperationException("The fake has no file state remaining.");
			}

			return _states.Dequeue();
		}

		public Task<AntigravityExecutableInspection> InspectAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			InspectionCount++;
			_calls.Add("inspect");

			if (_inspectionException is not null)
			{
				throw _inspectionException;
			}

			return Task.FromResult(_inspection);
		}
	}

	private sealed class FakeVersionProbe : IAntigravityCliVersionProbe
	{
		private readonly List<string> _calls;
		private readonly Exception? _exception;
		private readonly string _version;

		internal int CallCount { get; private set; }

		internal FakeVersionProbe(
			string version,
			List<string>? calls = null,
			Exception? exception = null)
		{
			_version = version;
			_calls = calls ?? new List<string>();
			_exception = exception;
		}

		public Task<string> ProbeAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;
			_calls.Add("probe");

			if (_exception is not null)
			{
				throw _exception;
			}

			return Task.FromResult(_version);
		}
	}

	private sealed class ReplacementAttemptingVersionProbe : IAntigravityCliVersionProbe
	{
		private readonly string _replacementPath;
		private readonly string _version;

		internal bool WasReplacementBlocked { get; private set; }

		internal bool UnknownReplacementWasExecuted { get; private set; }

		internal ReplacementAttemptingVersionProbe(
			string replacementPath,
			string version)
		{
			_replacementPath = replacementPath;
			_version = version;
		}

		public Task<string> ProbeAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				File.Move(_replacementPath, absolutePath, overwrite: true);
			}
			catch (Exception exception) when (
				(exception is IOException) ||
				(exception is UnauthorizedAccessException))
			{
				WasReplacementBlocked = true;
			}

			if (!WasReplacementBlocked)
			{
				UnknownReplacementWasExecuted = true;
				throw new InvalidOperationException(
					"The test replacement reached the simulated execution boundary.");
			}

			return Task.FromResult(_version);
		}
	}

	private const string CliVersion = "1.1.3";
	private const string FileVersion = "1.1.3.0";
	private const string ProductVersion = "1.1.3";
	private const string SignerSubject = "CN=Google LLC, O=Google LLC, C=US";
	private static readonly string Sha256 = new('A', 64);
	private static readonly string SignerThumbprint = new('B', 40);
	private static readonly AntigravityExecutableInspection TrustedInspection = new(
		FileVersion,
		ProductVersion,
		Sha256,
		0,
		SignerSubject,
		SignerThumbprint);
	private static readonly AntigravityExecutableFileState UnchangedFileState = new(
		2,
		new DateTime(2026, 7, 16, 1, 2, 3, DateTimeKind.Utc),
		new DateTime(2026, 7, 16, 1, 2, 4, DateTimeKind.Utc),
		FileAttributes.Archive,
		false);

	[Fact]
	public async Task ValidateAsync_WithExactFingerprint_ProbesOnlyAfterInspectionAndReturnsSupported()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		List<string> calls = new();
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 3),
			TrustedInspection,
			calls);
		FakeVersionProbe versionProbe = new(CliVersion, calls);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		using AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.True(result.IsSupported);
		Assert.Equal(AntigravityCliCapabilityFailureReason.None, result.FailureReason);
		Assert.Equal(
			new[] { "capture", "inspect", "capture", "probe", "capture" },
			calls);
		Assert.Equal(CreateFingerprint(executablePath), result.ObservedFingerprint);
		Assert.NotNull(result.ExecutableLease);
		Assert.False(result.ExecutableLease.IsDisposed);
		Assert.Equal(executablePath, result.ExecutableLease.AbsolutePath);
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData("", " \t")]
	public async Task ValidateAsync_WithExplicitAbsentVersionsAndMissingPeVersions_ReturnsSupported(
		string? fileVersion,
		string? productVersion)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityExecutableInspection inspection = TrustedInspection with
		{
			FileVersion = fileVersion,
			ProductVersion = productVersion
		};
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 3),
			inspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliFingerprint fingerprint = CreateFingerprint(executablePath) with
		{
			FileVersion = AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			ProductVersion = AntigravityCliCapabilityValidator.AbsentVersionSentinel
		};
		AntigravityCliCapabilityValidator validator = new(
			new[] { fingerprint },
			inspector,
			versionProbe);

		using AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.True(result.IsSupported);
		Assert.Equal(1, versionProbe.CallCount);
		Assert.Equal(
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			result.ObservedFingerprint?.FileVersion);
		Assert.Equal(
			AntigravityCliCapabilityValidator.AbsentVersionSentinel,
			result.ObservedFingerprint?.ProductVersion);
	}

	[Theory]
	[InlineData("file-version", FileVersion)]
	[InlineData("file-version", AntigravityCliCapabilityValidator.AbsentVersionSentinel)]
	[InlineData("product-version", ProductVersion)]
	[InlineData("product-version", AntigravityCliCapabilityValidator.AbsentVersionSentinel)]
	public async Task ValidateAsync_WithAbsentSentinelButPresentPeVersion_FailsClosedBeforeProbe(
		string field,
		string inspectedVersion)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityExecutableInspection inspection = field switch
		{
			"file-version" => TrustedInspection with { FileVersion = inspectedVersion },
			"product-version" => TrustedInspection with { ProductVersion = inspectedVersion },
			_ => throw new ArgumentOutOfRangeException(nameof(field))
		};
		AntigravityCliFingerprint fingerprint = field switch
		{
			"file-version" => CreateFingerprint(executablePath) with
			{
				FileVersion = AntigravityCliCapabilityValidator.AbsentVersionSentinel
			},
			"product-version" => CreateFingerprint(executablePath) with
			{
				ProductVersion = AntigravityCliCapabilityValidator.AbsentVersionSentinel
			},
			_ => throw new ArgumentOutOfRangeException(nameof(field))
		};
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 2),
			inspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = new(
			new[] { fingerprint },
			inspector,
			versionProbe);

		using AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
			result.FailureReason);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Theory]
	[InlineData("file-version")]
	[InlineData("product-version")]
	public async Task ValidateAsync_WithPresentAllowlistedVersionButMissingPeVersion_FailsClosedBeforeProbe(
		string field)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityExecutableInspection inspection = field switch
		{
			"file-version" => TrustedInspection with { FileVersion = null },
			"product-version" => TrustedInspection with { ProductVersion = " \t" },
			_ => throw new ArgumentOutOfRangeException(nameof(field))
		};
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 2),
			inspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		using AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
			result.FailureReason);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Theory]
	[InlineData("file-version")]
	[InlineData("product-version")]
	public void Constructor_WithImplicitlyMissingAllowlistedVersion_RejectsConfiguration(
		string field)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityCliFingerprint fingerprint = field switch
		{
			"file-version" => CreateFingerprint(executablePath) with
			{
				FileVersion = string.Empty
			},
			"product-version" => CreateFingerprint(executablePath) with
			{
				ProductVersion = " \t"
			},
			_ => throw new ArgumentOutOfRangeException(nameof(field))
		};

		Assert.Throws<ArgumentException>(() =>
			new AntigravityCliCapabilityValidator(new[] { fingerprint }));
	}

	[Fact]
	public void ConPtyVersionProbeEnvironment_DisablesAutoUpdate()
	{
		IReadOnlyDictionary<string, string> environment =
			ConPtyAntigravityCliVersionProbe.BuildEnvironmentAllowlist();

		Assert.Equal("true", environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
	}

	[Fact]
	public async Task ValidateAsync_SupportedResultRetainsLeaseUntilDisposed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string replacementPath = Path.Combine(
			temporaryDirectory.Path,
			"replacement.exe");
		File.WriteAllBytes(replacementPath, new byte[] { 0x4D, 0x5A, 0x01 });
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 3),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		using AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);
		AntigravityExecutableLease lease = Assert.IsType<AntigravityExecutableLease>(
			result.ExecutableLease);

		Exception? replacementFailure = Record.Exception(() =>
			File.Move(replacementPath, executablePath, overwrite: true));
		Assert.True(
			(replacementFailure is IOException) ||
			(replacementFailure is UnauthorizedAccessException),
			$"Unexpected replacement outcome: {replacementFailure}");
		Assert.False(lease.IsDisposed);

		result.Dispose();

		Assert.True(lease.IsDisposed);
		Assert.Null(result.ExecutableLease);
		File.Move(replacementPath, executablePath, overwrite: true);
		Assert.Equal(
			new byte[] { 0x4D, 0x5A, 0x01 },
			File.ReadAllBytes(executablePath));
	}

	[Fact]
	public async Task ValidateAsync_WithRelativePath_RejectsBeforeInspectionOrProbe()
	{
		FakeExecutableInspector inspector = new(
			Array.Empty<AntigravityExecutableFileState>(),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = new(
			Array.Empty<AntigravityCliFingerprint>(),
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync("agy.exe");

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.InvalidExecutablePath,
			result.FailureReason);
		Assert.Equal(0, inspector.CaptureCount);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithCommandWrapper_RejectsBeforeInspectionOrProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string wrapperPath = Path.Combine(temporaryDirectory.Path, "agy.cmd");
		File.WriteAllText(wrapperPath, "@echo off");
		FakeExecutableInspector inspector = new(
			Array.Empty<AntigravityExecutableFileState>(),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(wrapperPath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.InvalidExecutablePath,
			result.FailureReason);
		Assert.Equal(0, inspector.CaptureCount);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithMissingExecutable_FailsClosed()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = Path.GetFullPath(
			Path.Combine(temporaryDirectory.Path, "missing-agy.exe"));
		FakeExecutableInspector inspector = new(
			Array.Empty<AntigravityExecutableFileState>(),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.ExecutableNotFound,
			result.FailureReason);
		Assert.Equal(0, inspector.CaptureCount);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithReparsePoint_FailsClosedBeforeInspectionOrProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityExecutableFileState reparseState = UnchangedFileState with
		{
			Attributes = FileAttributes.Archive | FileAttributes.ReparsePoint,
			HasReparsePoint = true
		};
		FakeExecutableInspector inspector = new(
			new[] { reparseState },
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.ReparsePoint,
			result.FailureReason);
		Assert.Equal(0, inspector.InspectionCount);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WhenFileChangesDuringInspection_DoesNotExecuteVersionProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityExecutableFileState changedState = UnchangedFileState with
		{
			Length = UnchangedFileState.Length + 1
		};
		FakeExecutableInspector inspector = new(
			new[] { UnchangedFileState, changedState },
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.ExecutableChanged,
			result.FailureReason);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Theory]
	[InlineData("file-version")]
	[InlineData("product-version")]
	[InlineData("sha256")]
	[InlineData("signer-subject")]
	[InlineData("signer-thumbprint")]
	public async Task ValidateAsync_WithMetadataMismatch_DoesNotExecuteVersionProbe(
		string mismatch)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityExecutableInspection inspection = mismatch switch
		{
			"file-version" => TrustedInspection with { FileVersion = "1.1.4.0" },
			"product-version" => TrustedInspection with { ProductVersion = "1.1.4" },
			"sha256" => TrustedInspection with { Sha256 = new string('C', 64) },
			"signer-subject" => TrustedInspection with { SignerSubject = "CN=Unknown" },
			"signer-thumbprint" => TrustedInspection with
			{
				SignerThumbprint = new string('D', 40)
			},
			_ => throw new ArgumentOutOfRangeException(nameof(mismatch))
		};
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 2),
			inspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
			result.FailureReason);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WhenWinVerifyTrustRejects_DoesNotExecuteVersionProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 2),
			TrustedInspection with
			{
				WinVerifyTrustStatus = unchecked((int)0x800B0100)
			});
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.AuthenticodeNotTrusted,
			result.FailureReason);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithUnknownPath_DoesNotInspectOrExecuteVersionProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string allowlistedPath = CreateExecutable(temporaryDirectory);
		string unknownPath = Path.Combine(temporaryDirectory.Path, "other-agy.exe");
		File.WriteAllBytes(unknownPath, new byte[] { 0x4D, 0x5A });
		FakeExecutableInspector inspector = new(
			Array.Empty<AntigravityExecutableFileState>(),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			allowlistedPath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(unknownPath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.FingerprintNotAllowlisted,
			result.FailureReason);
		Assert.Equal(0, inspector.CaptureCount);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithUnknownCliVersion_FailsClosedAfterProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string replacementPath = Path.Combine(
			temporaryDirectory.Path,
			"replacement.exe");
		File.WriteAllBytes(replacementPath, new byte[] { 0x4D, 0x5A, 0x02 });
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 3),
			TrustedInspection);
		FakeVersionProbe versionProbe = new("1.1.4");
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.CliVersionNotAllowlisted,
			result.FailureReason);
		Assert.Equal(1, versionProbe.CallCount);
		Assert.Equal("1.1.4", result.ObservedFingerprint?.CliVersion);
		Assert.Null(result.ExecutableLease);
		File.Move(replacementPath, executablePath, overwrite: true);
		Assert.Equal(
			new byte[] { 0x4D, 0x5A, 0x02 },
			File.ReadAllBytes(executablePath));
	}

	[Fact]
	public async Task ValidateAsync_WhenReplacementIsAttemptedAtProbeBoundary_DoesNotExecuteUnknownFile()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		string replacementPath = Path.Combine(
			temporaryDirectory.Path,
			"unknown.exe");
		File.WriteAllBytes(replacementPath, new byte[] { 0x4D, 0x5A, 0xFF });
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 3),
			TrustedInspection);
		ReplacementAttemptingVersionProbe versionProbe = new(
			replacementPath,
			CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		using AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.True(result.IsSupported);
		Assert.True(versionProbe.WasReplacementBlocked);
		Assert.False(versionProbe.UnknownReplacementWasExecuted);
		Assert.True(File.Exists(replacementPath));
		Assert.Equal(
			new byte[] { 0x4D, 0x5A },
			File.ReadAllBytes(executablePath));
	}

	[Fact]
	public async Task ValidateAsync_WhenInspectionFails_DoesNotExecuteVersionProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			new[] { UnchangedFileState },
			TrustedInspection,
			inspectionException: new IOException("inspection failed"));
		FakeVersionProbe versionProbe = new(CliVersion);
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.InspectionFailed,
			result.FailureReason);
		Assert.Equal(0, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WhenVersionProbeFails_ReturnsUnsupported()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 2),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(
			CliVersion,
			exception: new AntigravityCliVersionProbeException(
				AntigravityCliVersionProbeFailureReason.TimedOut));
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.VersionProbeFailed,
			result.FailureReason);
		Assert.Equal(
			(AntigravityCliVersionProbeFailureReason?)
				AntigravityCliVersionProbeFailureReason.TimedOut,
			result.VersionProbeFailureReason);
		Assert.Equal(1, versionProbe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithMalformedVersionOutput_ReturnsFixedSubreason()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			RepeatState(UnchangedFileState, 3),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(" \r\n ");
		AntigravityCliCapabilityValidator validator = CreateValidator(
			executablePath,
			inspector,
			versionProbe);

		AntigravityCliCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityCliCapabilityFailureReason.VersionProbeFailed,
			result.FailureReason);
		Assert.Equal(
			(AntigravityCliVersionProbeFailureReason?)
				AntigravityCliVersionProbeFailureReason.MalformedOutput,
			result.VersionProbeFailureReason);
		Assert.Equal(1, versionProbe.CallCount);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   \r\n")]
	[InlineData("fixture-1.2.3\r\nsecond-line\r\n")]
	[InlineData("\r\nfixture-1.2.3\r\n")]
	[InlineData(" fixture-1.2.3\r\n")]
	[InlineData("\tfixture-1.2.3\r\n")]
	[InlineData("fixture-1.2.3\u00a0\r\n")]
	[InlineData("\u001b[?9999hfixture-1.2.3\r\n")]
	[InlineData("\u001b[31")]
	[InlineData("\u001b[0cfixture-1.2.3\r\n")]
	[InlineData("\u001b[?1004hfixture-1.2.3\r\n")]
	[InlineData("\u001b[?2004hfixture-1.2.3\r\n")]
	[InlineData("\u001b[?9001hfixture-1.2.3\r\n")]
	[InlineData("\u001b[?1049hfixture-1.2.3\r\n")]
	[InlineData("warning-text!\r\u001b[2Kfixture-1.2.3\r\n")]
	[InlineData("warning-text!\u001bcfixture-1.2.3\r\n")]
	[InlineData("warning-text!\rfixture-1.2.3\r\n")]
	[InlineData("\u001b[?1049hwarning\u001b[?1049lfixture-1.2.3\r\n")]
	[InlineData("\u001b[?2004h\u001b[?2004lfixture-1.2.3\r\n")]
	public void VersionOutputRenderer_WithAmbiguousOrUnsupportedVt_FailsClosed(
		string rawOutput)
	{
		AntigravityCliVersionProbeException exception = Assert.Throws<
			AntigravityCliVersionProbeException>(() =>
				AntigravityCliVersionOutputRenderer.Render(
					Encoding.UTF8.GetBytes(rawOutput)));

		Assert.Equal(
			AntigravityCliVersionProbeFailureReason.MalformedOutput,
			exception.FailureReason);
	}

	[Fact]
	public void VersionOutputRenderer_WithActiveModifyOtherKeysLevelTwo_FailsClosed()
	{
		const string rawOutput =
			"\u001b[>4;2mfixture-1.2.3\r\n";

		AntigravityCliVersionProbeException exception = Assert.Throws<
			AntigravityCliVersionProbeException>(() =>
				AntigravityCliVersionOutputRenderer.Render(
					Encoding.UTF8.GetBytes(rawOutput)));

		Assert.Equal(
			AntigravityCliVersionProbeFailureReason.MalformedOutput,
			exception.FailureReason);
	}

	[Fact]
	public void VersionOutputRenderer_WithActiveKittyKeyboardFlagsOne_FailsClosed()
	{
		const string rawOutput =
			"\u001b[=1;1ufixture-1.2.3\r\n";

		AntigravityCliVersionProbeException exception = Assert.Throws<
			AntigravityCliVersionProbeException>(() =>
				AntigravityCliVersionOutputRenderer.Render(
					Encoding.UTF8.GetBytes(rawOutput)));

		Assert.Equal(
			AntigravityCliVersionProbeFailureReason.MalformedOutput,
			exception.FailureReason);
	}

	[Fact]
	public void VersionOutputRenderer_WithKittyKeyboardFlagsQuery_FailsClosed()
	{
		const string rawOutput =
			"\u001b[?ufixture-1.2.3\r\n";

		AntigravityCliVersionProbeException exception = Assert.Throws<
			AntigravityCliVersionProbeException>(() =>
				AntigravityCliVersionOutputRenderer.Render(
					Encoding.UTF8.GetBytes(rawOutput)));

		Assert.Equal(
			AntigravityCliVersionProbeFailureReason.MalformedOutput,
			exception.FailureReason);
	}

	[Theory]
	[InlineData("\u001b[ qfixture-1.2.3\r\n")]
	[InlineData("\u001b[2 qfixture-1.2.3\r\n")]
	public void VersionOutputRenderer_WithDecscusr_FailsClosed(string rawOutput)
	{
		AntigravityCliVersionProbeException exception = Assert.Throws<
			AntigravityCliVersionProbeException>(() =>
				AntigravityCliVersionOutputRenderer.Render(
					Encoding.UTF8.GetBytes(rawOutput)));

		Assert.Equal(
			AntigravityCliVersionProbeFailureReason.MalformedOutput,
			exception.FailureReason);
	}

	[Fact]
	public void VersionOutputRenderer_WithBalancedConHostWrapper_ReturnsVersion()
	{
		const string rawOutput =
			"\u001b[c" +
			"\u001b[?9001h\u001b[?1004h\u001b[?25l" +
			"\u001b[2J\u001b[m\u001b[H" +
			"\u001b[32mfixture-1.2.3\u001b[0m\r\n" +
			"\u001b[?25h\u001b[?1004l\u001b[?9001l";

		string version = AntigravityCliVersionOutputRenderer.Render(
			Encoding.UTF8.GetBytes(rawOutput));

		Assert.Equal("fixture-1.2.3", version);
	}

	[Fact]
	public void VersionOutputRenderer_WhenWarningScrollsOffscreen_FailsClosed()
	{
		string rawOutput =
			"warning" +
			string.Concat(Enumerable.Repeat("\r\n", 25)) +
			"\u001b[Hfixture-1.2.3\r\n";

		AntigravityCliVersionProbeException exception = Assert.Throws<
			AntigravityCliVersionProbeException>(() =>
				AntigravityCliVersionOutputRenderer.Render(
					Encoding.UTF8.GetBytes(rawOutput)));

		Assert.Equal(
			AntigravityCliVersionProbeFailureReason.MalformedOutput,
			exception.FailureReason);
	}

	[Fact]
	public void VersionOutputRenderer_WithInvalidUtf8_ReturnsFixedSubreason()
	{
		AntigravityCliVersionProbeException exception = Assert.Throws<
			AntigravityCliVersionProbeException>(() =>
				AntigravityCliVersionOutputRenderer.Render(
					new byte[] { 0xC3, 0x28 }));

		Assert.Equal(
			AntigravityCliVersionProbeFailureReason.InvalidUtf8,
			exception.FailureReason);
	}

	[Fact]
	public void Constructor_WithUntrustedAllowlistEntry_RejectsConfiguration()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityCliFingerprint untrusted = CreateFingerprint(executablePath) with
		{
			WinVerifyTrustStatus = unchecked((int)0x800B0100)
		};
		FakeExecutableInspector inspector = new(
			Array.Empty<AntigravityExecutableFileState>(),
			TrustedInspection);
		FakeVersionProbe versionProbe = new(CliVersion);

		Assert.Throws<ArgumentException>(() =>
			new AntigravityCliCapabilityValidator(
				new[] { untrusted },
				inspector,
				versionProbe));
	}

	[Fact]
	public async Task WindowsInspector_WithUnsignedTemporaryExe_ReportsHashAndUntrustedStatus()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		WindowsAntigravityExecutableInspector inspector = new();

		AntigravityExecutableFileState state =
			inspector.CaptureFileState(executablePath);
		AntigravityExecutableInspection inspection = await inspector.InspectAsync(
			executablePath,
			CancellationToken.None);

		Assert.False(state.HasReparsePoint);
		Assert.Equal(2, state.Length);
		Assert.Equal(
			Convert.ToHexString(SHA256.HashData(new byte[] { 0x4D, 0x5A })),
			inspection.Sha256);
		Assert.NotEqual(0, inspection.WinVerifyTrustStatus);
		Assert.Null(inspection.SignerSubject);
		Assert.Null(inspection.SignerThumbprint);
	}

	[Fact]
	public async Task WindowsInspector_WithSignedDotNetHost_ReportsTrustedMicrosoftSigner()
	{
		string executablePath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			"dotnet",
			"dotnet.exe");
		Assert.True(
			File.Exists(executablePath),
			$"The signed-system-executable fixture is unavailable: {executablePath}");
		WindowsAntigravityExecutableInspector inspector = new();

		AntigravityExecutableInspection inspection = await inspector.InspectAsync(
			executablePath,
			CancellationToken.None);

		Assert.True(
			inspection.WinVerifyTrustStatus == 0,
			$"Cache-only whole-chain Authenticode verification failed with status 0x{unchecked((uint)inspection.WinVerifyTrustStatus):X8}.");
		Assert.NotNull(inspection.SignerSubject);
		Assert.True(
			inspection.SignerSubject.Contains(
				"Microsoft",
				StringComparison.OrdinalIgnoreCase),
			$"Unexpected signer subject: {inspection.SignerSubject}");
		Assert.Matches("^[0-9A-F]{40}$", inspection.SignerThumbprint);
	}

	private static string CreateExecutable(TemporaryDirectory temporaryDirectory)
	{
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A });
		return Path.GetFullPath(executablePath);
	}

	private static AntigravityCliFingerprint CreateFingerprint(string executablePath)
	{
		return new AntigravityCliFingerprint(
			Path.GetFullPath(executablePath),
			CliVersion,
			FileVersion,
			ProductVersion,
			Sha256,
			0,
			SignerSubject,
			SignerThumbprint);
	}

	private static AntigravityCliCapabilityValidator CreateValidator(
		string executablePath,
		IAntigravityExecutableInspector inspector,
		IAntigravityCliVersionProbe versionProbe)
	{
		return new AntigravityCliCapabilityValidator(
			new[] { CreateFingerprint(executablePath) },
			inspector,
			versionProbe);
	}

	private static IEnumerable<AntigravityExecutableFileState> RepeatState(
		AntigravityExecutableFileState state,
		int count)
	{
		return Enumerable.Repeat(state, count);
	}
}
