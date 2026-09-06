using System.Text;

using AiUsageDashboard.Core.Claude;
using AiUsageDashboard.Licensing;

namespace AiUsageDashboard.ClaudeCapture;

internal static class Program
{
	internal const int MaximumInputBytes = 1024 * 1024;
	private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);

	private static async Task<int> Main(string[] args)
	{
		if (LegalCommandLine.IsLegalCommand(args))
		{
			try
			{
				LegalCommandLine.TryHandle(args, LegalCatalog.Load(LegalProfile.ClaudeCapture),
					LegalAcceptanceStore.CreateDefault, out int legalExitCode);
				return legalExitCode;
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine($"License command failed: {exception.Message}");
				return LegalCommandLine.FailureExitCode;
			}
		}

		string statusLine = "[Claude] capture unavailable";

		try
		{
			int legalExitCode = LegalCallbackGate.CheckAcceptance(
				() => LegalCatalog.Load(LegalProfile.ClaudeCapture), LegalAcceptanceStore.CreateDefault);
			if (legalExitCode != 0)
			{
				return legalExitCode;
			}

			string outputPath = ReadOutputPath(args);
			string json = await ReadBoundedInputAsync(
				Console.OpenStandardInput(),
				CancellationToken.None);

			ClaudeStatusLineCapture capture = ClaudeStatusLineParser.Parse(
				json,
				DateTimeOffset.UtcNow);
			ClaudeStatusLineCaptureStore store = new(outputPath);
			await store.WriteAsync(capture);
			statusLine = ClaudeStatusLineFormatter.Format(capture);
		}
		catch (Exception)
		{
			// The status line must never echo input, file paths, or exception details.
		}

		await Console.Out.WriteLineAsync(statusLine);
		return 0;
	}

	internal static async Task<string> ReadBoundedInputAsync(
		Stream input,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		byte[] buffer = new byte[8192];
		using MemoryStream content = new(buffer.Length);

		while (true)
		{
			int bytesRead = await input.ReadAsync(buffer, cancellationToken);

			if (bytesRead == 0)
			{
				break;
			}

			if ((content.Length + bytesRead) > MaximumInputBytes)
			{
				throw new InvalidDataException(
					"Claude status line input is too large.");
			}

			await content.WriteAsync(
				buffer.AsMemory(0, bytesRead),
				cancellationToken);
		}

		return StrictUtf8Encoding.GetString(
			content.GetBuffer(),
			0,
			checked((int)content.Length));
	}

	private static string ReadOutputPath(string[] args)
	{
		int outputOptionIndex = (args.Length == 3) &&
			string.Equals(
				args[0],
				"--ai-usage-dashboard-claude-capture",
				StringComparison.Ordinal)
			? 1
			: 0;
		if ((args.Length != (outputOptionIndex + 2)) ||
			!string.Equals(args[outputOptionIndex], "--output", StringComparison.Ordinal) ||
			string.IsNullOrWhiteSpace(args[outputOptionIndex + 1]))
		{
			throw new ArgumentException(
				"Expected the optional ownership marker, then --output and a capture file path.");
		}

		return args[outputOptionIndex + 1];
	}
}
