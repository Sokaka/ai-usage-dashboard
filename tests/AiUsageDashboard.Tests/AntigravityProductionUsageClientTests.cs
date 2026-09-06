using System.Text.Json;

using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class AntigravityProductionUsageClientTests
{
	private const string ExpectedAccountIdentity =
		"private.production.identity@example.invalid";
	private static readonly string[] ExpectedWindowIds =
	{
		"agy.gemini.weekly",
		"agy.gemini.rolling-5h",
		"agy.claude.weekly",
		"agy.claude.rolling-5h"
	};

	[Fact]
	public void PublicContract_ExposesOnlyApprovedSafeProperties()
	{
		string[] resultProperties = typeof(AntigravityProductionUsageResult)
			.GetProperties()
			.Select(property => property.Name)
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();
		string[] windowProperties =
			typeof(AntigravityProductionUsageWindow)
				.GetProperties()
				.Select(property => property.Name)
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToArray();

		Assert.Equal(
			new[]
			{
				"AccountIdentity",
				"AutomaticRevalidationPending",
				"FailureKind",
				"IsSuccessful",
				"SafetyFailureReason",
				"Windows"
			},
			resultProperties);
		Assert.Equal(
			new[]
			{
				"IsAvailable",
				"RemainingPercent",
				"ResetsIn",
				"StableWindowId"
			},
			windowProperties);
		Assert.Empty(typeof(AntigravityProductionUsageResult).GetConstructors());
	}

	[Fact]
	public async Task CaptureAsync_WithSafeSuccessfulRun_ReturnsExactlyFourWindows()
	{
		string privatePathSentinel = Path.Combine(
			Path.GetTempPath(),
			"PRIVATE.PRODUCTION.PROFILE.SENTINEL.json");
		AntigravityLiveR1SectionRunResult runResult =
			CreateSuccessfulRunResult();
		string? observedPath = null;
		AntigravityProductionUsageClient client = new(
			(path, _) =>
			{
				observedPath = path;
				return Task.FromResult(
					new AntigravityProductionUsageCoreResult(
						AntigravityProductionUsageFailureKind.None,
						runResult));
			});

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			privatePathSentinel,
			CancellationToken.None);
		string serialized = JsonSerializer.Serialize(result);

		Assert.True(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.None,
			result.FailureKind);
		Assert.Equal(ExpectedAccountIdentity, result.AccountIdentity);
		Assert.Equal(privatePathSentinel, observedPath);
		Assert.Equal(
			ExpectedWindowIds,
			result.Windows.Select(window => window.StableWindowId));
		Assert.Equal(new[] { 80m, 60m, 40m, 20m },
			result.Windows.Select(window => window.RemainingPercent));
		Assert.Equal(
			new[] { false, true, false, true },
			result.Windows.Select(window => window.IsAvailable));
		Assert.Equal(TimeSpan.FromHours(2), result.Windows[0].ResetsIn);
		Assert.Null(result.Windows[1].ResetsIn);
		Assert.Contains(
			ExpectedAccountIdentity,
			serialized,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			privatePathSentinel,
			serialized,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"quota_available",
			serialized,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Page", serialized, StringComparison.Ordinal);
		Assert.DoesNotContain("Report", serialized, StringComparison.Ordinal);
		IList<AntigravityProductionUsageWindow> list =
			Assert.IsAssignableFrom<IList<AntigravityProductionUsageWindow>>(
				result.Windows);
		Assert.Throws<NotSupportedException>(() => list.Add(result.Windows[0]));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" identity@example.invalid")]
	[InlineData("identity@example.invalid ")]
	[InlineData("IDENTITY@EXAMPLE.INVALID")]
	[InlineData("identity\u0001@example.invalid")]
	public async Task CaptureAsync_WhenIdentityIsNotSemanticParserNormalized_FailsClosed(
		string identity)
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityProductionUsageClient client = CreateClient(new(
			successful.Report,
			successful.Page! with { AccountIdentity = identity }));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Null(result.AccountIdentity);
		Assert.Empty(result.Windows);
		Assert.DoesNotContain(
			ExpectedAccountIdentity,
			JsonSerializer.Serialize(result),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task CaptureAsync_WhenRequiredGateIsNotPassed_FailsClosed()
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityLiveR0Report unsafeSafety =
			successful.Report.SafetyReport with
			{
				InputWriteCount = 0,
				InputWriteAttemptCount = 0
			};
		AntigravityLiveR1SectionRunResult unsafeRun = new(
			successful.Report with { SafetyReport = unsafeSafety },
			successful.Page);
		AntigravityProductionUsageClient client = CreateClient(unsafeRun);

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.CaptureRejected,
			result.FailureKind);
		Assert.Null(result.AccountIdentity);
		Assert.Empty(result.Windows);
		Assert.DoesNotContain(
			ExpectedAccountIdentity,
			JsonSerializer.Serialize(result),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task CaptureAsync_WhenIdentityExceedsReviewedMaximum_FailsClosed()
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityProductionUsageClient client = CreateClient(new(
			successful.Report,
			successful.Page! with { AccountIdentity = new string('a', 321) }));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Null(result.AccountIdentity);
		Assert.Empty(result.Windows);
	}

	[Fact]
	public async Task CaptureAsync_WhenPageIsPaginated_FailsClosed()
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityUsageR1SectionPage paginatedPage = successful.Page! with
		{
			Pagination = new AntigravityUsageR1SectionPagination(1, 20, 40)
		};
		AntigravityProductionUsageClient client = CreateClient(new(
			successful.Report,
			paginatedPage));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Empty(result.Windows);
	}

	[Fact]
	public async Task CaptureAsync_WhenPaginationPolicyDrifts_FailsClosed()
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityUsageR1SectionSemanticCapture changedCapture =
			successful.Report.UsageSemanticCapture! with
			{
				PaginationPolicy =
					(AntigravityUsageR1SectionPaginationPolicy)999
			};
		AntigravityProductionUsageClient client = CreateClient(new(
			successful.Report with { UsageSemanticCapture = changedCapture },
			successful.Page));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Empty(result.Windows);
	}

	[Fact]
	public async Task CaptureAsync_WhenStableWindowIdDrifts_FailsClosed()
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityUsageR1SectionQuotaSection firstSection =
			successful.Page!.Sections[0];
		AntigravityUsageR1SectionQuotaWindow[] changedWindows =
			firstSection.Windows.ToArray();
		changedWindows[0] = changedWindows[0] with
		{
			StableWindowId = "agy.unreviewed.weekly"
		};
		AntigravityUsageR1SectionQuotaSection[] changedSections =
			successful.Page.Sections.ToArray();
		changedSections[0] = firstSection with { Windows = changedWindows };
		AntigravityProductionUsageClient client = CreateClient(new(
			successful.Report,
			successful.Page with { Sections = changedSections }));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Empty(result.Windows);
	}

	[Fact]
	public async Task CaptureAsync_WhenRollingWindowDurationDrifts_FailsClosed()
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityUsageR1SectionQuotaSection firstSection =
			successful.Page!.Sections[0];
		AntigravityUsageR1SectionQuotaWindow[] changedWindows =
			firstSection.Windows.ToArray();
		changedWindows[1] = changedWindows[1] with { WindowHours = 6 };
		AntigravityUsageR1SectionQuotaSection[] changedSections =
			successful.Page.Sections.ToArray();
		changedSections[0] = firstSection with { Windows = changedWindows };
		AntigravityProductionUsageClient client = CreateClient(new(
			successful.Report,
			successful.Page with { Sections = changedSections }));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			result.FailureKind);
		Assert.Empty(result.Windows);
	}

	[Fact]
	public async Task CaptureAsync_WhenBaselineContractMismatchIsReported_ReturnsProvenanceFailure()
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityLiveR0Report mismatchSafety =
			successful.Report.SafetyReport with
			{
				IsCaptureSuccessful = false,
				InputWriteCount = 0,
				InputWriteAttemptCount = 0,
				FailureReasons = new[]
				{
					AntigravityLiveR0FailureReason
						.SettingsBaselineContractMismatch
				}
			};
		AntigravityProductionUsageClient client = CreateClient(new(
			successful.Report with
			{
				IsCaptureSuccessful = false,
				SafetyReport = mismatchSafety
			},
			page: null));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProvenanceRejected,
			result.FailureKind);
		Assert.Empty(result.Windows);
	}

	[Fact]
	public async Task CaptureAsync_WithRelativeProfilePath_RejectsBeforeCore()
	{
		int callCount = 0;
		AntigravityProductionUsageClient client = new(
			(_, _) =>
			{
				callCount++;
				return Task.FromResult(
					new AntigravityProductionUsageCoreResult(
						AntigravityProductionUsageFailureKind.None,
						CreateSuccessfulRunResult()));
			});

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			"relative-private-profile.json",
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProfileRejected,
			result.FailureKind);
		Assert.Empty(result.Windows);
		Assert.Equal(0, callCount);
	}

	[Fact]
	public async Task CaptureAsync_WithMissingPrivateProfile_ReturnsFixedProfileFailure()
	{
		AntigravityProductionUsageClient client = new();
		string missingPath = Path.Combine(
			Path.GetTempPath(),
			Guid.NewGuid().ToString("N"),
			"private-profile.json");

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			missingPath,
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.ProfileRejected,
			result.FailureKind);
		Assert.Empty(result.Windows);
		Assert.DoesNotContain(
			missingPath,
			JsonSerializer.Serialize(result),
			StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task CaptureAsync_WhenCancelled_PropagatesCancellation()
	{
		using CancellationTokenSource source = new();
		source.Cancel();
		AntigravityProductionUsageClient client = CreateClient(
			CreateSuccessfulRunResult());

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(CreateAbsoluteProfilePath(), source.Token));
	}

	[Fact]
	public async Task CaptureAsync_WhenCoreIsCancelled_PropagatesCancellation()
	{
		AntigravityProductionUsageClient client = new(
			(_, _) => throw new OperationCanceledException());

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(
				CreateAbsoluteProfilePath(),
				CancellationToken.None));
	}

	[Fact]
	public async Task CaptureAsync_WhenCancelledAfterInputWrite_LatchesProfile()
	{
		using CancellationTokenSource source = new();
		int callCount = 0;
		AntigravityLiveR1SectionRunResult rejected =
			CreateRejectedRunResult(inputWriteAttemptCount: 1);
		AntigravityProductionUsageClient client = new(
			(_, _) =>
			{
				callCount++;
				source.Cancel();
				return Task.FromResult(
					new AntigravityProductionUsageCoreResult(
						AntigravityProductionUsageFailureKind.None,
						rejected));
			});
		string profilePath = CreateAbsoluteProfilePath();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(profilePath, source.Token));
		AntigravityProductionUsageResult second = await client.CaptureAsync(
			profilePath,
			CancellationToken.None);

		Assert.False(second.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(1, callCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenCancelledBeforeInputWrite_AllowsRetry()
	{
		using CancellationTokenSource source = new();
		int callCount = 0;
		AntigravityLiveR1SectionRunResult rejected =
			CreateRejectedRunResult(inputWriteAttemptCount: 0);
		AntigravityProductionUsageClient client = new(
			(_, _) =>
			{
				callCount++;

				if (callCount == 1)
				{
					source.Cancel();
				}

				return Task.FromResult(
					new AntigravityProductionUsageCoreResult(
						AntigravityProductionUsageFailureKind.None,
						rejected));
			});
		string profilePath = CreateAbsoluteProfilePath();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.CaptureAsync(profilePath, source.Token));
		AntigravityProductionUsageResult second = await client.CaptureAsync(
			profilePath,
			CancellationToken.None);

		Assert.False(second.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.CaptureRejected,
			second.FailureKind);
		Assert.Equal(2, callCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenPostWriteCaptureFails_LatchesBeforeRetry()
	{
		int callCount = 0;
		AntigravityProductionUsageClient client = new(
			(_, _) =>
			{
				callCount++;
				return Task.FromResult(
					new AntigravityProductionUsageCoreResult(
						AntigravityProductionUsageFailureKind.None,
						CreateRejectedRunResult(inputWriteAttemptCount: 1)));
			});
		string profilePath = CreateAbsoluteProfilePath();

		AntigravityProductionUsageResult first = await client.CaptureAsync(
			profilePath,
			CancellationToken.None);
		AntigravityProductionUsageResult second = await client.CaptureAsync(
			profilePath,
			CancellationToken.None);

		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			first.FailureKind);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.SafetyLatched,
			second.FailureKind);
		Assert.Equal(1, callCount);
	}

	[Fact]
	public async Task CaptureAsync_WhenCoreReturnsUnknownFailureKind_NormalizesFailure()
	{
		AntigravityProductionUsageClient client = new(
			(_, _) => Task.FromResult(
				new AntigravityProductionUsageCoreResult(
					(AntigravityProductionUsageFailureKind)999,
					RunResult: null)));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.UnexpectedFailure,
			result.FailureKind);
		Assert.Empty(result.Windows);
	}

	[Fact]
	public async Task CaptureAsync_WhenCoreThrows_ReturnsFixedUnexpectedFailure()
	{
		AntigravityProductionUsageClient client = new(
			(_, _) => throw new InvalidOperationException(
				"PRIVATE.EXCEPTION.SENTINEL"));

		AntigravityProductionUsageResult result = await client.CaptureAsync(
			CreateAbsoluteProfilePath(),
			CancellationToken.None);

		Assert.False(result.IsSuccessful);
		Assert.Equal(
			AntigravityProductionUsageFailureKind.UnexpectedFailure,
			result.FailureKind);
		Assert.Empty(result.Windows);
		Assert.DoesNotContain(
			"PRIVATE.EXCEPTION.SENTINEL",
			JsonSerializer.Serialize(result),
			StringComparison.Ordinal);
	}

	private static AntigravityProductionUsageClient CreateClient(
		AntigravityLiveR1SectionRunResult runResult)
	{
		return new AntigravityProductionUsageClient(
			(_, _) => Task.FromResult(
				new AntigravityProductionUsageCoreResult(
					AntigravityProductionUsageFailureKind.None,
					runResult)));
	}

	private static AntigravityLiveR1SectionRunResult
		CreateSuccessfulRunResult()
	{
		const string LayoutId = "approved-production-layout";
		string pageFingerprint = new('A', 64);
		AntigravityUsageR1SectionQuotaWindow[] geminiWindows =
		{
			CreateWindow(
				ExpectedWindowIds[0],
				AntigravityUsageR1SectionWindowKind.Weekly,
				windowHours: null,
				remainingPercent: 80m,
				resetsIn: TimeSpan.FromHours(2)),
			CreateWindow(
				ExpectedWindowIds[1],
				AntigravityUsageR1SectionWindowKind.RollingHours,
				windowHours: 5,
				remainingPercent: 60m,
				resetsIn: null)
		};
		AntigravityUsageR1SectionQuotaWindow[] claudeWindows =
		{
			CreateWindow(
				ExpectedWindowIds[2],
				AntigravityUsageR1SectionWindowKind.Weekly,
				windowHours: null,
				remainingPercent: 40m,
				resetsIn: TimeSpan.FromMinutes(30)),
			CreateWindow(
				ExpectedWindowIds[3],
				AntigravityUsageR1SectionWindowKind.RollingHours,
				windowHours: 5,
				remainingPercent: 20m,
				resetsIn: null)
		};
		AntigravityUsageR1SectionQuotaSection[] sections =
		{
			new("agy.gemini", geminiWindows),
			new("agy.claude", claudeWindows)
		};
		AntigravityUsageR1SectionPage page = new(
			LayoutId,
			pageFingerprint,
			ExpectedAccountIdentity,
			Pagination: null,
			sections);
		AntigravityUsageR1SectionSemanticCapture semanticCapture = new(
			LayoutId,
			new string('B', 64),
			pageFingerprint,
			AntigravityUsageR1SectionPaginationPolicy.RequireFullyVisible,
			new[] { "agy.gemini", "agy.claude" },
			ExpectedWindowIds,
			SectionCount: 2,
			WindowCount: 4);
		AntigravityLiveR0Report safety = new(
			AntigravityLiveR0Mode.CaptureUsage,
			IsCaptureSuccessful: true,
			IsR0Go: false,
			ExistingProcessDetected: false,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.Passed,
			AntigravityLiveR0GateStatus.NotVerified,
			AntigravityLiveR0GateStatus.NotVerified,
			InputWriteCount: 1,
			PromptExactLocalFingerprint: "PRIVATE.PROMPT.FINGERPRINT",
			PromptCapture: null,
			UsageCapture: null,
			FailureReasons: Array.Empty<AntigravityLiveR0FailureReason>(),
			InputWriteAttemptCount: 1);
		AntigravityLiveR1SectionReport report = new(
			IsCaptureSuccessful: true,
			IsR1Go: false,
			safety,
			semanticCapture);
		return new AntigravityLiveR1SectionRunResult(report, page);
	}

	private static AntigravityLiveR1SectionRunResult CreateRejectedRunResult(
		int inputWriteAttemptCount)
	{
		AntigravityLiveR1SectionRunResult successful =
			CreateSuccessfulRunResult();
		AntigravityLiveR0Report rejectedSafety =
			successful.Report.SafetyReport with
			{
				IsCaptureSuccessful = false,
				UsageCaptureGate = AntigravityLiveR0GateStatus.Failed,
				InputWriteCount = inputWriteAttemptCount,
				InputWriteAttemptCount = inputWriteAttemptCount,
				FailureReasons = new[]
				{
					AntigravityLiveR0FailureReason
						.UsageSemanticLayoutNotObserved
				}
			};
		return new AntigravityLiveR1SectionRunResult(
			successful.Report with
			{
				IsCaptureSuccessful = false,
				SafetyReport = rejectedSafety,
				UsageSemanticCapture = null
			},
			null);
	}

	private static AntigravityUsageR1SectionQuotaWindow CreateWindow(
		string stableWindowId,
		AntigravityUsageR1SectionWindowKind windowKind,
		int? windowHours,
		decimal remainingPercent,
		TimeSpan? resetsIn)
	{
		return new AntigravityUsageR1SectionQuotaWindow(
			stableWindowId,
			windowKind,
			windowHours,
			new AntigravityUsageR1SectionCapacity(
				UsedPercent: 100m - remainingPercent,
				remainingPercent,
				AvailabilityStatusId: null),
			new AntigravityUsageR1SectionReset(
				resetsIn,
				resetsIn.HasValue ? null : "quota_available"));
	}

	private static string CreateAbsoluteProfilePath()
	{
		return Path.Combine(Path.GetTempPath(), "synthetic-profile.json");
	}
}
