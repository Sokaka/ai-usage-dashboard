using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageDashboard.AntigravitySpike;

internal enum AntigravityUsagePageKind
{
	Single,
	Top,
	Middle,
	Bottom
}

internal sealed record AntigravityUsageLayout(
	string Id,
	string PanelTitle,
	string AccountPrefix,
	string PagePrefix,
	string TableHeader,
	string PanelFooter,
	string FieldSeparator,
	bool IsAlternateScreen,
	IReadOnlySet<string> AllowedModelIds,
	IReadOnlySet<string> RequiredModelIds,
	IReadOnlyDictionary<int, string> ExpectedSemanticFingerprints);

internal sealed record AntigravityQuotaRow(
	string StableModelId,
	int UsedPercent,
	int RemainingPercent,
	DateTimeOffset? ResetsAt);

internal sealed record AntigravityUsagePage(
	string LayoutId,
	string SemanticFingerprint,
	string AccountIdentity,
	AntigravityUsagePageKind PageKind,
	IReadOnlyList<string> RequiredModelIds,
	IReadOnlyList<AntigravityQuotaRow> Rows)
{
	internal bool IsAtTop =>
		(PageKind == AntigravityUsagePageKind.Single) ||
		(PageKind == AntigravityUsagePageKind.Top);

	internal bool IsAtBottom =>
		(PageKind == AntigravityUsagePageKind.Single) ||
		(PageKind == AntigravityUsagePageKind.Bottom);
}

internal sealed record AntigravityUsageResult(
	string LayoutId,
	string AccountIdentity,
	IReadOnlyList<AntigravityQuotaRow> Rows);

internal static class AntigravityUsageScreenParser
{
	private const int MaximumIdentityLength = 320;
	private const int MaximumModelRows = 64;
	private const int MaximumScreenLines = 256;

	internal static AntigravityUsagePage ParsePage(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageLayout layout)
	{
		return ParseCore(snapshot, layout, validateFingerprint: true);
	}

	internal static AntigravityUsagePage ParseCandidatePage(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageLayout layout)
	{
		return ParseCore(snapshot, layout, validateFingerprint: false);
	}

	private static AntigravityUsagePage ParseCore(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageLayout layout,
		bool validateFingerprint)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(layout);
		ValidateLayout(layout);

		if ((snapshot.Columns < 1) ||
			(snapshot.Columns > 1000) ||
			(snapshot.Rows < 1) ||
			(snapshot.Rows > MaximumScreenLines) ||
			(snapshot.Lines.Count != snapshot.Rows))
		{
			throw new InvalidDataException("Rendered AGY screen dimensions are invalid.");
		}

		if (snapshot.IsAlternateScreen != layout.IsAlternateScreen)
		{
			throw new InvalidDataException("AGY quota panel uses an uncalibrated screen-buffer mode.");
		}

		string[] lines = snapshot.Lines
			.Select(NormalizeRenderedLine)
			.ToArray();
		int[] titleIndexes = lines
			.Select((line, index) => (line, index))
			.Where(item => string.Equals(
				item.line,
				layout.PanelTitle,
				StringComparison.Ordinal))
			.Select(item => item.index)
			.ToArray();

		if (titleIndexes.Length != 1)
		{
			throw new InvalidDataException("AGY quota panel title is missing or ambiguous.");
		}

		int titleIndex = titleIndexes[0];
		int accountIndex = titleIndex + 1;
		int pageIndex = titleIndex + 2;
		int headerIndex = titleIndex + 3;

		if (headerIndex >= lines.Length)
		{
			throw new InvalidDataException("AGY quota panel is truncated before its table header.");
		}

		string accountIdentity = ParseIdentity(lines[accountIndex], layout.AccountPrefix);
		AntigravityUsagePageKind pageKind = ParsePageKind(lines[pageIndex], layout.PagePrefix);

		if (!string.Equals(lines[headerIndex], layout.TableHeader, StringComparison.Ordinal))
		{
			throw new InvalidDataException("AGY quota table header does not match the calibrated layout.");
		}

		int footerIndex = FindFooter(lines, headerIndex, layout.PanelFooter);
		List<AntigravityQuotaRow> rows = new();
		HashSet<string> modelIds = new(StringComparer.Ordinal);

		for (int index = headerIndex + 1; index < footerIndex; index++)
		{
			if (lines[index].Length == 0)
			{
				throw new InvalidDataException("AGY quota panel contains an unexpected blank row.");
			}

			AntigravityQuotaRow row = ParseRow(lines[index], layout);

			if (!modelIds.Add(row.StableModelId))
			{
				throw new InvalidDataException("AGY quota panel contains a duplicate model identity.");
			}

			rows.Add(row);

			if (rows.Count > MaximumModelRows)
			{
				throw new InvalidDataException("AGY quota panel exceeds the model-row safety limit.");
			}
		}

		if (rows.Count == 0)
		{
			throw new InvalidDataException("AGY quota panel contains no model rows.");
		}

		if ((pageKind == AntigravityUsagePageKind.Single) &&
			!layout.RequiredModelIds.All(modelIds.Contains))
		{
			throw new InvalidDataException("AGY quota panel is missing a required model bucket.");
		}

		string semanticFingerprint = ComputeFingerprint(
			snapshot,
			layout,
			titleIndex,
			headerIndex,
			lines);

		if (validateFingerprint &&
			(!layout.ExpectedSemanticFingerprints.TryGetValue(
				snapshot.Columns,
				out string? expectedFingerprint) ||
			 !string.Equals(
				semanticFingerprint,
				expectedFingerprint,
				StringComparison.Ordinal)))
		{
			throw new InvalidDataException("AGY quota layout is not allowlisted for this rendered viewport.");
		}

		ReadOnlyCollection<string> requiredModelIds = Array.AsReadOnly(
			layout.RequiredModelIds
				.OrderBy(modelId => modelId, StringComparer.Ordinal)
				.ToArray());
		return new AntigravityUsagePage(
			layout.Id,
			semanticFingerprint,
			accountIdentity,
			pageKind,
			requiredModelIds,
			rows.AsReadOnly());
	}

	private static string ComputeFingerprint(
		TerminalScreenSnapshot snapshot,
		AntigravityUsageLayout layout,
		int titleIndex,
		int headerIndex,
		IReadOnlyList<string> lines)
	{
		string canonicalStructure = string.Join(
			'\n',
			layout.Id,
			$"viewport:{snapshot.Columns}x{snapshot.Rows}",
			$"alternate:{snapshot.IsAlternateScreen}",
			$"title@{titleIndex}:{lines[titleIndex]}",
			$"account@{titleIndex + 1}:{layout.AccountPrefix}<identity>",
			$"page@{titleIndex + 2}:{layout.PagePrefix}<page-kind>",
			$"header@{headerIndex}:{lines[headerIndex]}",
			$"row:<model>{layout.FieldSeparator}<used>{layout.FieldSeparator}<remaining>{layout.FieldSeparator}<reset>",
			$"footer:<after-rows>:{layout.PanelFooter}");
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalStructure));
		return Convert.ToHexString(hash);
	}

	private static int FindFooter(
		IReadOnlyList<string> lines,
		int headerIndex,
		string expectedFooter)
	{
		int footerIndex = -1;

		for (int index = headerIndex + 1; index < lines.Count; index++)
		{
			if (!string.Equals(lines[index], expectedFooter, StringComparison.Ordinal))
			{
				continue;
			}

			if (footerIndex >= 0)
			{
				throw new InvalidDataException("AGY quota panel footer is ambiguous.");
			}

			footerIndex = index;
		}

		if (footerIndex < 0)
		{
			throw new InvalidDataException("AGY quota panel footer is missing.");
		}

		return footerIndex;
	}

	private static string NormalizeRenderedLine(string line)
	{
		ArgumentNullException.ThrowIfNull(line);

		if (line.Any(character =>
			char.IsControl(character) &&
			(character != '\t')))
		{
			throw new InvalidDataException("Rendered AGY screen still contains control characters.");
		}

		return line.TrimEnd(' ');
	}

	private static string ParseIdentity(string line, string prefix)
	{
		if (!line.StartsWith(prefix, StringComparison.Ordinal))
		{
			throw new InvalidDataException("AGY quota panel identity field is missing.");
		}

		string renderedIdentity = line[prefix.Length..];

		if (!string.Equals(
			renderedIdentity,
			renderedIdentity.Trim(),
			StringComparison.Ordinal))
		{
			throw new InvalidDataException("AGY quota panel identity spacing does not match the calibrated layout.");
		}

		string identity = renderedIdentity.Normalize(NormalizationForm.FormKC);

		if ((identity.Length == 0) ||
			(identity.Length > MaximumIdentityLength) ||
			identity.Any(char.IsControl))
		{
			throw new InvalidDataException("AGY quota panel identity is invalid.");
		}

		return identity.ToLowerInvariant();
	}

	private static AntigravityUsagePageKind ParsePageKind(string line, string prefix)
	{
		if (!line.StartsWith(prefix, StringComparison.Ordinal))
		{
			throw new InvalidDataException("AGY quota panel page marker is missing.");
		}

		return line[prefix.Length..] switch
		{
			"single" => AntigravityUsagePageKind.Single,
			"top" => AntigravityUsagePageKind.Top,
			"middle" => AntigravityUsagePageKind.Middle,
			"bottom" => AntigravityUsagePageKind.Bottom,
			_ => throw new InvalidDataException("AGY quota panel page marker is unknown.")
		};
	}

	private static AntigravityQuotaRow ParseRow(
		string line,
		AntigravityUsageLayout layout)
	{
		string[] fields = line.Split(
			layout.FieldSeparator,
			StringSplitOptions.None);

		if ((fields.Length != 4) ||
			fields.Any(field =>
				(field.Length == 0) ||
				!string.Equals(
					field,
					field.Trim(),
					StringComparison.Ordinal)))
		{
			throw new InvalidDataException("AGY quota row does not match the calibrated field structure.");
		}

		string modelId = fields[0];

		if (!layout.AllowedModelIds.Contains(modelId))
		{
			throw new InvalidDataException("AGY quota row contains an unknown model identity.");
		}

		if (fields.Any(field =>
			field.Contains("disabled", StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("Disabled AGY quota buckets are not calibrated.");
		}

		int usedPercent = ParsePercent(fields[1]);
		int remainingPercent = ParsePercent(fields[2]);

		if ((usedPercent + remainingPercent) != 100)
		{
			throw new InvalidDataException("AGY used and remaining percentages are inconsistent.");
		}

		return new AntigravityQuotaRow(
			modelId,
			usedPercent,
			remainingPercent,
			ParseReset(fields[3]));
	}

	private static int ParsePercent(string value)
	{
		if ((value.Length < 2) ||
			(value[^1] != '%') ||
			!int.TryParse(
				value.AsSpan(0, value.Length - 1),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out int percentage) ||
			(percentage < 0) ||
			(percentage > 100))
		{
			throw new InvalidDataException("AGY quota percentage is invalid.");
		}

		return percentage;
	}

	private static DateTimeOffset? ParseReset(string value)
	{
		if (string.Equals(value, "-", StringComparison.Ordinal))
		{
			return null;
		}

		if (!DateTimeOffset.TryParseExact(
			value,
			"O",
			CultureInfo.InvariantCulture,
			DateTimeStyles.None,
			out DateTimeOffset resetsAt))
		{
			throw new InvalidDataException("AGY quota reset value is invalid.");
		}

		return resetsAt;
	}

	private static void ValidateLayout(AntigravityUsageLayout layout)
	{
		if (string.IsNullOrWhiteSpace(layout.Id) ||
			string.IsNullOrWhiteSpace(layout.PanelTitle) ||
			string.IsNullOrWhiteSpace(layout.AccountPrefix) ||
			string.IsNullOrWhiteSpace(layout.PagePrefix) ||
			string.IsNullOrWhiteSpace(layout.TableHeader) ||
			string.IsNullOrWhiteSpace(layout.PanelFooter) ||
			string.IsNullOrWhiteSpace(layout.FieldSeparator) ||
			layout.FieldSeparator.Any(char.IsControl) ||
			(layout.AllowedModelIds is null) ||
			(layout.AllowedModelIds.Count == 0) ||
			(layout.RequiredModelIds is null) ||
			(layout.RequiredModelIds.Count == 0) ||
			(layout.ExpectedSemanticFingerprints is null) ||
			!layout.RequiredModelIds.All(layout.AllowedModelIds.Contains))
		{
			throw new ArgumentException("AGY usage layout is incomplete.", nameof(layout));
		}
	}
}

internal sealed class AntigravityUsagePageAccumulator
{
	private const int MaximumModels = 64;
	private const int MaximumPages = 16;
	private readonly Dictionary<string, AntigravityQuotaRow> _rows =
		new(StringComparer.Ordinal);
	private string? _accountIdentity;
	private string? _layoutId;
	private int _pageCount;
	private HashSet<string>? _requiredModelIds;
	private string? _semanticFingerprint;

	internal bool IsComplete { get; private set; }

	internal void AddPage(AntigravityUsagePage page)
	{
		ArgumentNullException.ThrowIfNull(page);

		if (IsComplete)
		{
			throw new InvalidOperationException("The AGY quota page sequence is already complete.");
		}

		if (_pageCount >= MaximumPages)
		{
			throw new InvalidDataException("AGY quota pagination exceeds the page safety limit.");
		}

		if ((_pageCount == 0) && !page.IsAtTop)
		{
			throw new InvalidDataException("AGY quota pagination did not begin at the top.");
		}

		if ((_pageCount > 0) && page.IsAtTop)
		{
			throw new InvalidDataException("AGY quota pagination unexpectedly returned to the top.");
		}

		string expectedLayoutId = _layoutId ?? page.LayoutId;
		string expectedAccountIdentity = _accountIdentity ?? page.AccountIdentity;
		string expectedSemanticFingerprint =
			_semanticFingerprint ?? page.SemanticFingerprint;
		HashSet<string> expectedRequiredModelIds = _requiredModelIds ??
			new HashSet<string>(page.RequiredModelIds, StringComparer.Ordinal);

		if (!string.Equals(expectedLayoutId, page.LayoutId, StringComparison.Ordinal) ||
			!string.Equals(
				expectedAccountIdentity,
				page.AccountIdentity,
				StringComparison.Ordinal) ||
			!string.Equals(
				expectedSemanticFingerprint,
				page.SemanticFingerprint,
				StringComparison.Ordinal) ||
			!expectedRequiredModelIds.SetEquals(page.RequiredModelIds))
		{
			throw new InvalidDataException("AGY quota pagination changed layout, fingerprint, required models, or account identity.");
		}

		Dictionary<string, AntigravityQuotaRow> newRows =
			new(StringComparer.Ordinal);
		HashSet<string> pageModelIds = new(StringComparer.Ordinal);

		foreach (AntigravityQuotaRow row in page.Rows)
		{
			if (!pageModelIds.Add(row.StableModelId))
			{
				throw new InvalidDataException("AGY quota page contains a duplicate model identity.");
			}

			if (_rows.TryGetValue(row.StableModelId, out AntigravityQuotaRow? existingRow))
			{
				if (existingRow != row)
				{
					throw new InvalidDataException("AGY quota pagination contains conflicting model values.");
				}

				continue;
			}

			newRows.Add(row.StableModelId, row);
		}

		if ((_rows.Count + newRows.Count) > MaximumModels)
		{
			throw new InvalidDataException("AGY quota pagination exceeds the model safety limit.");
		}

		if ((_pageCount > 0) && (newRows.Count == 0))
		{
			throw new InvalidDataException("AGY quota pagination made no forward progress.");
		}

		foreach ((string modelId, AntigravityQuotaRow row) in newRows)
		{
			_rows.Add(modelId, row);
		}

		_layoutId = expectedLayoutId;
		_accountIdentity = expectedAccountIdentity;
		_semanticFingerprint = expectedSemanticFingerprint;
		_requiredModelIds = expectedRequiredModelIds;
		_pageCount++;
		IsComplete = page.IsAtBottom;
	}

	internal AntigravityUsageResult Build()
	{
		if (!IsComplete ||
			(_layoutId is null) ||
			(_accountIdentity is null) ||
			(_requiredModelIds is null) ||
			(_rows.Count == 0))
		{
			throw new InvalidOperationException("AGY quota pagination is incomplete.");
		}

		if (!_requiredModelIds.All(_rows.ContainsKey))
		{
			throw new InvalidDataException("AGY quota pagination is missing a required model bucket.");
		}

		return new AntigravityUsageResult(
			_layoutId,
			_accountIdentity,
			Array.AsReadOnly(_rows.Values.ToArray()));
	}
}
