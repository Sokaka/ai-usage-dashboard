namespace AiUsageDashboard.App.Providers;

internal sealed record GrokPrincipal(
	string PrincipalType,
	string PrincipalId,
	string? Email = null);

internal sealed record GrokWeeklyUsage(
	double? UsedPercent,
	DateTimeOffset PeriodStartsAt,
	DateTimeOffset ResetsAt);

internal static class GrokPlanTierRules
{
	private const int MaximumPlanTierLength = 64;

	internal static string? Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) ||
			(value.Length > MaximumPlanTierLength) ||
			!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
			value.Any(char.IsControl))
		{
			return null;
		}

		return value;
	}
}

internal enum GrokUsageAvailability
{
	Available,
	Unsupported,
	TransientProbeError,
	AuthenticationRequired
}

internal enum GrokCurrentAuthUsability
{
	Usable,
	Unusable,
	Unknown
}

internal sealed record GrokUsagePollResult
{
	public GrokPrincipal Principal { get; }

	public GrokWeeklyUsage? WeeklyUsage { get; }

	public string? PlanTier { get; }

	public DateTimeOffset ObservedAt { get; }

	public CliVersionEvidence? VersionEvidence { get; init; }

	public GrokUsageAvailability UsageAvailability { get; }

	public GrokCurrentAuthUsability CurrentAuthUsability { get; }

	public bool ScopeObserved =>
		!string.IsNullOrWhiteSpace(Principal.PrincipalType) &&
		!string.IsNullOrWhiteSpace(Principal.PrincipalId);

	public bool UsageAvailable =>
		UsageAvailability == GrokUsageAvailability.Available;

	public bool CurrentAuthUsable =>
		CurrentAuthUsability == GrokCurrentAuthUsability.Usable;

	public bool CanCommitBinding => ScopeObserved && CurrentAuthUsable;

	public GrokUsagePollResult(
		GrokPrincipal principal,
		GrokWeeklyUsage? weeklyUsage,
		DateTimeOffset observedAt,
		CliVersionEvidence? versionEvidence = null,
		GrokUsageAvailability usageAvailability =
			GrokUsageAvailability.Available,
		GrokCurrentAuthUsability currentAuthUsability =
			GrokCurrentAuthUsability.Usable,
		string? planTier = null)
	{
		ArgumentNullException.ThrowIfNull(principal);

		if (string.IsNullOrWhiteSpace(principal.PrincipalType) ||
			string.IsNullOrWhiteSpace(principal.PrincipalId) ||
			!string.Equals(
				principal.PrincipalType,
				principal.PrincipalType.Trim(),
				StringComparison.Ordinal) ||
			!string.Equals(
				principal.PrincipalId,
				principal.PrincipalId.Trim(),
				StringComparison.Ordinal) ||
			principal.PrincipalType.Any(char.IsControl) ||
			principal.PrincipalId.Any(char.IsControl))
		{
			throw new ArgumentException(
				"Grok principal 格式無效。",
				nameof(principal));
		}

		if (!Enum.IsDefined(usageAvailability))
		{
			throw new ArgumentOutOfRangeException(nameof(usageAvailability));
		}

		if (!Enum.IsDefined(currentAuthUsability))
		{
			throw new ArgumentOutOfRangeException(nameof(currentAuthUsability));
		}

		bool isValidState = usageAvailability switch
		{
			GrokUsageAvailability.Available =>
				(weeklyUsage is not null) &&
				(currentAuthUsability == GrokCurrentAuthUsability.Usable),
			GrokUsageAvailability.Unsupported =>
				(weeklyUsage is null) &&
				(currentAuthUsability == GrokCurrentAuthUsability.Usable),
			GrokUsageAvailability.TransientProbeError =>
				(weeklyUsage is null) &&
				(currentAuthUsability is
					GrokCurrentAuthUsability.Usable or
					GrokCurrentAuthUsability.Unknown),
			GrokUsageAvailability.AuthenticationRequired =>
				(weeklyUsage is null) &&
				(currentAuthUsability == GrokCurrentAuthUsability.Unusable),
			_ => false
		};

		if (!isValidState)
		{
			throw new ArgumentException("Grok usage 與 authentication 狀態互相矛盾。");
		}

		Principal = principal;
		WeeklyUsage = weeklyUsage;
		PlanTier = GrokPlanTierRules.Normalize(planTier);
		ObservedAt = observedAt;
		VersionEvidence = versionEvidence;
		UsageAvailability = usageAvailability;
		CurrentAuthUsability = currentAuthUsability;
	}
}
