using System.Globalization;

using AiUsageDashboard.Core.Models;

namespace AiUsageDashboard.App.ViewModels;

public enum UsageLevel
{
	Normal,
	Warning,
	Critical
}

public sealed class UsageMetricViewModel
{
	private const string OrganizationQuotaLimitUnavailableText =
		"（此來源未提供上限）";
	private const string OrganizationQuotaScopeToolTipText =
		"Business／Enterprise 的 organization AI Credits 與預算不包含在這個來源中，請以 GitHub Copilot settings 為準。";
	private const string OverageEnabledStatusText = "額外用量已開啟";
	private const string UsageAllowedAfterQuotaStatusText = "額度用完後仍可使用";

	public double DisplayPercent { get; }

	public string DisplayValue { get; }

	public bool HasResetText { get; }

	public bool HasUsageBar { get; }

	public bool IsResetImminent { get; }

	public string Key { get; }

	public string Label { get; }

	public UsageLevel Level { get; }

	public string ProgressAutomationName { get; }

	public string ResetText { get; }

	public string ToolTipValue { get; }

	public double UsedPercent { get; }

	public UsageMetricViewModel(UsageMetric metric)
		: this(metric, UsageDisplayMode.Used, TimeProvider.System)
	{
	}

	public UsageMetricViewModel(
		UsageMetric metric,
		UsageDisplayMode usageDisplayMode)
		: this(metric, usageDisplayMode, TimeProvider.System)
	{
	}

	internal UsageMetricViewModel(
		UsageMetric metric,
		TimeProvider timeProvider)
		: this(metric, UsageDisplayMode.Used, timeProvider)
	{
	}

	internal UsageMetricViewModel(
		UsageMetric metric,
		UsageDisplayMode usageDisplayMode,
		TimeProvider timeProvider)
		: this(metric, usageDisplayMode, timeProvider, showResetText: true)
	{
	}

	internal UsageMetricViewModel(
		UsageMetric metric,
		UsageDisplayMode usageDisplayMode,
		bool showResetText,
		bool usePercentageDisplayValue = false)
		: this(
			metric,
			usageDisplayMode,
			TimeProvider.System,
			showResetText,
			usePercentageDisplayValue)
	{
	}

	private UsageMetricViewModel(
		UsageMetric metric,
		UsageDisplayMode usageDisplayMode,
		TimeProvider timeProvider,
		bool showResetText,
		bool usePercentageDisplayValue = false)
	{
		ArgumentNullException.ThrowIfNull(metric);
		ArgumentNullException.ThrowIfNull(timeProvider);

		if (!Enum.IsDefined(usageDisplayMode))
		{
			throw new ArgumentOutOfRangeException(
				nameof(usageDisplayMode),
				usageDisplayMode,
				"未知的用量顯示方式。");
		}

		Key = metric.Key;
		Label = UsageMetricPresentation.GetDisplayLabel(metric);
		HasUsageBar = metric.UsedPercent is not null;
		UsedPercent = Math.Clamp(metric.UsedPercent ?? 0, 0, 100);
		string extractedUsageStatusText = usePercentageDisplayValue
			? GetUsageStatusText(metric.DisplayValue)
			: string.Empty;
		bool shouldUsePercentageDisplayValue = usePercentageDisplayValue &&
			!UsesAbsoluteAiCreditsDisplay(metric);
		string primaryDisplayValue = GetPrimaryDisplayValue(
			metric.DisplayValue,
			extractedUsageStatusText);
		DisplayPercent = (usageDisplayMode == UsageDisplayMode.Remaining) &&
			HasUsageBar
			? 100 - UsedPercent
			: UsedPercent;
		DisplayValue = HasUsageBar &&
			((usageDisplayMode == UsageDisplayMode.Remaining) ||
				shouldUsePercentageDisplayValue)
			? CreatePercentageDisplayValue(
				usageDisplayMode,
				DisplayPercent)
			: NormalizeUsedDisplayValue(
				primaryDisplayValue,
				HasUsageBar);
		string defaultToolTipValue = usePercentageDisplayValue && HasUsageBar
			? primaryDisplayValue
			: DisplayValue;
		ToolTipValue = HasUnreportedOrganizationQuotaLimit(
				metric,
				primaryDisplayValue)
			? $"{defaultToolTipValue}。{OrganizationQuotaScopeToolTipText}"
			: defaultToolTipValue;
		ProgressAutomationName = usageDisplayMode == UsageDisplayMode.Remaining
			? $"{Label}，剩餘"
			: $"{Label}，已使用";
		Level = UsedPercent switch
		{
			>= 100 => UsageLevel.Critical,
			> 80 => UsageLevel.Warning,
			_ => UsageLevel.Normal
		};
		ResetText = !string.IsNullOrWhiteSpace(metric.ResetDisplayValue)
			? metric.ResetDisplayValue
			: metric.ResetsAt is null
				? "未提供重置時間"
				: $"重置 · {metric.ResetsAt.Value.ToLocalTime():MM/dd HH:mm}";
		HasResetText = showResetText &&
			(HasUsageBar || (metric.ResetsAt is not null)) &&
			!string.IsNullOrWhiteSpace(ResetText);
		IsResetImminent = HasResetText && UsageMetricPresentation.IsResetImminent(
			metric,
			timeProvider.GetUtcNow());
	}

	private static string CreatePercentageDisplayValue(
		UsageDisplayMode usageDisplayMode,
		double displayPercent)
	{
		string prefix = usageDisplayMode == UsageDisplayMode.Remaining
			? "剩餘"
			: "已使用";
		return $"{prefix} " +
			$"{displayPercent.ToString("0.##", CultureInfo.InvariantCulture)}%";
	}

	private static string GetPrimaryDisplayValue(
		string displayValue,
		string usageStatusText)
	{
		return string.IsNullOrEmpty(usageStatusText)
			? displayValue
			: displayValue[..^(usageStatusText.Length + 1)];
	}

	private static bool HasUnreportedOrganizationQuotaLimit(
		UsageMetric metric,
		string displayValue)
	{
		return (metric.Key is
				"copilot-quota-premium-interactions" or
				"copilot-quota-chat") &&
			displayValue.Contains(
				OrganizationQuotaLimitUnavailableText,
				StringComparison.Ordinal);
	}

	private static string GetUsageStatusText(string displayValue)
	{
		if (displayValue.EndsWith(
			$"，{OverageEnabledStatusText}",
			StringComparison.Ordinal))
		{
			return OverageEnabledStatusText;
		}

		return displayValue.EndsWith(
			$"，{UsageAllowedAfterQuotaStatusText}",
			StringComparison.Ordinal)
			? UsageAllowedAfterQuotaStatusText
			: string.Empty;
	}

	private static string NormalizeUsedDisplayValue(
		string displayValue,
		bool hasUsageBar)
	{
		const string UsedPrefix = "已使用 ";

		return hasUsageBar && displayValue.StartsWith(
			UsedPrefix,
			StringComparison.Ordinal)
			? $"已使用 {displayValue[UsedPrefix.Length..]}"
			: displayValue;
	}

	private static bool UsesAbsoluteAiCreditsDisplay(UsageMetric metric)
	{
		return string.Equals(
			metric.Key,
			"copilot-quota-premium-interactions",
			StringComparison.Ordinal) &&
			string.Equals(metric.Label, "AI Credits", StringComparison.Ordinal);
	}
}
