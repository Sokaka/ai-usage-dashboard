using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityOfficialPrintPolicyTests
{
	private sealed class FakeExecutableInspector :
		IAntigravityExecutableInspector
	{
		private readonly AntigravityExecutableInspection _inspection;
		private readonly Queue<AntigravityExecutableFileState> _states;

		internal int CaptureCount { get; private set; }

		internal int InspectionCount { get; private set; }

		internal int ProvenanceInspectionCount { get; private set; }

		internal FakeExecutableInspector(
			IEnumerable<AntigravityExecutableFileState> states,
			AntigravityExecutableInspection inspection)
		{
			_states = new Queue<AntigravityExecutableFileState>(states);
			_inspection = inspection;
		}

		public AntigravityExecutableFileState CaptureFileState(
			string absolutePath)
		{
			CaptureCount++;

			if (_states.Count == 0)
			{
				throw new InvalidOperationException(
					"The fake has no file state remaining.");
			}

			return _states.Dequeue();
		}

		public Task<AntigravityExecutableInspection> InspectAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			InspectionCount++;
			return Task.FromResult(_inspection);
		}

		public Task<AntigravityExecutableInspection> InspectProvenanceAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ProvenanceInspectionCount++;
			return Task.FromResult(_inspection with { Sha256 = string.Empty });
		}
	}

	private sealed class FakeVersionProbe : IAntigravityCliVersionProbe
	{
		private readonly string _version;

		internal int CallCount { get; private set; }

		internal FakeVersionProbe(string version)
		{
			_version = version;
		}

		public Task<string> ProbeAsync(
			string absolutePath,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CallCount++;
			return Task.FromResult(_version);
		}
	}

	private static readonly AntigravityExecutableFileState StableFileState = new(
		Length: 2,
		CreationTimeUtc: new DateTime(
			2026,
			8,
			12,
			0,
			0,
			0,
			DateTimeKind.Utc),
		LastWriteTimeUtc: new DateTime(
			2026,
			8,
			12,
			0,
			0,
			1,
			DateTimeKind.Utc),
		Attributes: FileAttributes.Archive,
		HasReparsePoint: false);
	private static readonly AntigravityExecutableInspection TrustedInspection =
		new(
			FileVersion: null,
			ProductVersion: null,
			Sha256: new string('A', 64),
			WinVerifyTrustStatus: 0,
			SignerSubject:
				AntigravityOfficialPrintCapabilityValidator.RequiredSignerSubject,
			SignerThumbprint:
				AntigravityOfficialPrintCapabilityValidator.RequiredSignerThumbprint);

	[Fact]
	public async Task ValidateAsync_WithPinnedSignerAndVersion1112_ReturnsSupported()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			Enumerable.Repeat(StableFileState, 3),
			TrustedInspection);
		FakeVersionProbe probe = new("1.1.12");
		AntigravityOfficialPrintCapabilityValidator validator = new(
			inspector,
			probe);

		using AntigravityOfficialPrintCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.True(result.IsSupported);
		Assert.Equal("1.1.12", result.CliVersion);
		Assert.Equal(
			AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed,
			result.VersionAssessment?.Kind);
		Assert.Equal("1.1.12", result.VersionAssessment?.DetectedVersion);
		Assert.True(result.VersionAssessment?.IsExecutionAllowed);
		Assert.NotNull(result.ExecutableLease);
		Assert.False(result.ExecutableLease.IsDisposed);
		Assert.Equal(executablePath, result.ExecutableLease.AbsolutePath);
		Assert.Equal(3, inspector.CaptureCount);
		Assert.Equal(0, inspector.InspectionCount);
		Assert.Equal(1, inspector.ProvenanceInspectionCount);
		Assert.Equal(1, probe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithWrongSigner_RejectsBeforeVersionProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			Enumerable.Repeat(StableFileState, 2),
			TrustedInspection with
			{
				SignerThumbprint = new string('B', 40)
			});
		FakeVersionProbe probe = new("1.1.12");
		AntigravityOfficialPrintCapabilityValidator validator = new(
			inspector,
			probe);

		using AntigravityOfficialPrintCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Null(result.VersionAssessment);
		Assert.Null(result.ExecutableLease);
		Assert.Equal(2, inspector.CaptureCount);
		Assert.Equal(0, inspector.InspectionCount);
		Assert.Equal(1, inspector.ProvenanceInspectionCount);
		Assert.Equal(0, probe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithReparsePoint_RejectsBeforeInspection()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		AntigravityExecutableFileState reparseState = StableFileState with
		{
			Attributes = FileAttributes.Archive | FileAttributes.ReparsePoint,
			HasReparsePoint = true
		};
		FakeExecutableInspector inspector = new(
			new[] { reparseState },
			TrustedInspection);
		FakeVersionProbe probe = new("1.1.12");
		AntigravityOfficialPrintCapabilityValidator validator = new(
			inspector,
			probe);

		using AntigravityOfficialPrintCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(1, inspector.CaptureCount);
		Assert.Equal(0, inspector.InspectionCount);
		Assert.Equal(0, inspector.ProvenanceInspectionCount);
		Assert.Equal(0, probe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WhenFileStateChanges_RejectsBeforeVersionProbe()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			new[]
			{
				StableFileState,
				StableFileState with { Length = StableFileState.Length + 1 }
			},
			TrustedInspection);
		FakeVersionProbe probe = new("1.1.12");
		AntigravityOfficialPrintCapabilityValidator validator = new(
			inspector,
			probe);

		using AntigravityOfficialPrintCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(2, inspector.CaptureCount);
		Assert.Equal(0, inspector.InspectionCount);
		Assert.Equal(1, inspector.ProvenanceInspectionCount);
		Assert.Equal(0, probe.CallCount);
	}

	[Fact]
	public async Task ValidateAsync_WithVersion1110_RejectsAfterProvenanceChecks()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string executablePath = CreateExecutable(temporaryDirectory);
		FakeExecutableInspector inspector = new(
			Enumerable.Repeat(StableFileState, 3),
			TrustedInspection);
		FakeVersionProbe probe = new("1.1.10");
		AntigravityOfficialPrintCapabilityValidator validator = new(
			inspector,
			probe);

		using AntigravityOfficialPrintCapabilityValidationResult result =
			await validator.ValidateAsync(executablePath);

		Assert.False(result.IsSupported);
		Assert.Equal(
			AntigravityOfficialPrintVersionAssessmentKind
				.MissingRequiredCapability,
			result.VersionAssessment?.Kind);
		Assert.Equal("1.1.10", result.VersionAssessment?.DetectedVersion);
		Assert.False(result.VersionAssessment?.IsExecutionAllowed);
		Assert.Null(result.ExecutableLease);
		Assert.Equal(3, inspector.CaptureCount);
		Assert.Equal(0, inspector.InspectionCount);
		Assert.Equal(1, inspector.ProvenanceInspectionCount);
		Assert.Equal(1, probe.CallCount);
	}

	[Fact]
	public void AssessVersion_ClassifiesCompatibilityAndSafetyBoundaries()
	{
		(string? Version,
		 AntigravityOfficialPrintVersionAssessmentKind ExpectedKind,
		 bool ExpectedExecutionAllowed)[] cases =
		[
			(AntigravityOfficialPrintCapabilityValidator.ReferenceVersion,
			 AntigravityOfficialPrintVersionAssessmentKind.Reference,
			 true),
			("1.1.12", AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed, true),
			("1.2.0", AntigravityOfficialPrintVersionAssessmentKind.UnverifiedAllowed, true),
			("1.1.10", AntigravityOfficialPrintVersionAssessmentKind.MissingRequiredCapability, false),
			("1.0.999", AntigravityOfficialPrintVersionAssessmentKind.MissingRequiredCapability, false),
			("0.9.0", AntigravityOfficialPrintVersionAssessmentKind.MissingRequiredCapability, false),
			("2.0.0", AntigravityOfficialPrintVersionAssessmentKind.SafetyBoundaryRejected, false),
			("1.1.11-beta.1", AntigravityOfficialPrintVersionAssessmentKind.SafetyBoundaryRejected, false),
			(null, AntigravityOfficialPrintVersionAssessmentKind.SafetyBoundaryRejected, false)
		];

		foreach (var testCase in cases)
		{
			AntigravityOfficialPrintVersionAssessment assessment =
				AntigravityOfficialPrintCapabilityValidator
					.AssessVersion(testCase.Version);

			Assert.Equal(testCase.ExpectedKind, assessment.Kind);
			Assert.Equal(testCase.Version, assessment.DetectedVersion);
			Assert.Equal(
				testCase.ExpectedExecutionAllowed,
				assessment.IsExecutionAllowed);
			Assert.Equal(
				testCase.ExpectedExecutionAllowed,
				AntigravityOfficialPrintCapabilityValidator
					.IsSupportedVersion(testCase.Version));
		}
	}

	[Theory]
	[InlineData("1.1.11")]
	[InlineData("1.1.12")]
	[InlineData("1.2.0")]
	[InlineData("1.99.999")]
	public void IsSupportedVersion_AcceptsSupportedStableOneX(
		string version)
	{
		Assert.True(
			AntigravityOfficialPrintCapabilityValidator
				.IsSupportedVersion(version));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("1.1.10")]
	[InlineData("1.0.999")]
	[InlineData("0.9.0")]
	[InlineData("2.0.0")]
	[InlineData("v1.1.11")]
	[InlineData("1.1.11-beta.1")]
	[InlineData("1.1")]
	[InlineData("1.1.11.0")]
	[InlineData("01.1.11")]
	[InlineData("1.01.11")]
	[InlineData("1.1.011")]
	[InlineData(" 1.1.11")]
	[InlineData("1.1.11 ")]
	[InlineData("1.1.11\r\n2.0.0")]
	public void IsSupportedVersion_RejectsAnythingOutsideCanonicalContract(
		string? version)
	{
		Assert.False(
			AntigravityOfficialPrintCapabilityValidator
				.IsSupportedVersion(version));
	}

	[Fact]
	public void OfficialPolicy_PinsExpectedGoogleSigner()
	{
		Assert.Equal(
			"607A3EDAA64933E94422FC8F0C80388E0590986C",
			AntigravityOfficialPrintCapabilityValidator
				.RequiredSignerThumbprint);
		Assert.StartsWith(
			"CN=Google LLC, O=Google LLC,",
			AntigravityOfficialPrintCapabilityValidator.RequiredSignerSubject,
			StringComparison.Ordinal);
	}

	private static string CreateExecutable(
		TemporaryDirectory temporaryDirectory)
	{
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A });
		return Path.GetFullPath(executablePath);
	}
}
