using System.Text.Json;

namespace AiUsageDashboard.PrivacyCheck;

internal static class Program
{
	private const int FailureExitCode = 1;
	private const int MaximumReportedViolations = 50;

	private static async Task<int> Main(string[] args)
	{
		using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(10));
		string operation = (args.Length > 0) && (args[0] is "history" or "diagnostics")
			? args[0] : "parse command";
		try
		{
			if (args.Length == 0)
			{
				throw new PrivacyCheckException("Expected history or diagnostics command.");
			}
			Dictionary<string, string> options = ParseOptions(args);
			switch (args[0])
			{
				case "history":
					return await ScanHistoryAsync(options, cancellation.Token).ConfigureAwait(false);
				case "diagnostics":
					return PrepareDiagnostics(options);
				default:
					throw new PrivacyCheckException("Expected history or diagnostics command.");
			}
		}
		catch (DiagnosticPreparer.PreparationException exception)
		{
			Console.Error.WriteLine(JsonSerializer.Serialize(new
			{
				Operation = "prepare diagnostics",
				exception.RelativePath,
				exception.LineNumber,
				exception.Rule
			}));
			return FailureExitCode;
		}
		catch (PrivacyCheckException exception)
		{
			Console.Error.WriteLine(JsonSerializer.Serialize(new { Operation = operation, exception.Message }));
			return FailureExitCode;
		}
		catch (Exception exception)
		{
			// 原始 Git/XML 例外可能含私密內容；只輸出本工具的安全摘要。
			Console.Error.WriteLine(JsonSerializer.Serialize(new
			{
				Operation = operation,
				ErrorType = exception.GetType().Name,
				Message = "Privacy validation failed; no successful scan or safe diagnostics may be inferred. Check repository completeness, input encodings, limits, and command arguments."
			}));
			return FailureExitCode;
		}
	}

	private static Dictionary<string, string> ParseOptions(string[] args)
	{
		Dictionary<string, string> options = new(StringComparer.Ordinal);
		for (int index = 1; index < args.Length; index++)
		{
			string name = args[index];
			if (name == "--all")
			{
				if (!options.TryAdd(name, string.Empty))
				{
					throw new PrivacyCheckException("Duplicate --all option.");
				}
				continue;
			}
			if (!name.StartsWith("--", StringComparison.Ordinal) || (++index >= args.Length) ||
				args[index].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(name, args[index]))
			{
				throw new PrivacyCheckException("Command options must be unique names with explicit values.");
			}
		}
		return options;
	}

	private static async Task<int> ScanHistoryAsync(
		Dictionary<string, string> options,
		CancellationToken cancellationToken)
	{
		AssertAllowedOptions(options, "--repository", "--revision", "--base", "--all");
		string repositoryPath = RequiredOption(options, "--repository");
		if (options.ContainsKey("--all") && (options.ContainsKey("--revision") || options.ContainsKey("--base")))
		{
			throw new PrivacyCheckException("--all cannot be combined with --revision or --base.");
		}
		HistoryScanOptions scanOptions = new(repositoryPath,
			options.ContainsKey("--all") ? null : options.GetValueOrDefault("--revision", "HEAD"),
			options.GetValueOrDefault("--base"));
		HistoryScanResult result = await GitHistoryScanner.ScanAsync(scanOptions, cancellationToken).ConfigureAwait(false);
		Console.WriteLine(JsonSerializer.Serialize(new
		{
			result.Scope,
			result.Commits,
			result.UniqueBlobs,
			result.TextBlobs,
			result.BinaryBlobs,
			result.TotalBytes,
			ViolationCount = result.Violations.Count,
			Violations = result.Violations.Take(MaximumReportedViolations)
		}));
		return result.Violations.Count == 0 ? 0 : FailureExitCode;
	}

	private static int PrepareDiagnostics(Dictionary<string, string> options)
	{
		AssertAllowedOptions(options, "--input", "--output");
		IReadOnlyList<string> preparedPaths = DiagnosticPreparer.Prepare(
			RequiredOption(options, "--input"), RequiredOption(options, "--output"));
		Console.WriteLine(JsonSerializer.Serialize(new { PreparedFiles = preparedPaths.Count }));
		return 0;
	}

	private static void AssertAllowedOptions(Dictionary<string, string> options, params string[] allowed)
	{
		if (options.Keys.Any(name => !allowed.Contains(name, StringComparer.Ordinal)))
		{
			throw new PrivacyCheckException("Unsupported option for the selected privacy command.");
		}
	}

	private static string RequiredOption(Dictionary<string, string> options, string name)
	{
		if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
		{
			throw new PrivacyCheckException($"Privacy command requires {name}.");
		}
		return value;
	}
}
