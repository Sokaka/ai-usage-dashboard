using System.Security.Cryptography;
using System.Text;

using AiUsageDashboard.Updater.Core;

namespace AiUsageDashboard.FeedSigning;

internal static class Program
{
	private static int Main(string[] args)
	{
		try
		{
			if (args.Length == 0)
			{
				throw new ArgumentException("Expected validate-trust, sign or verify command.");
			}
			Dictionary<string, string> options = ParseOptions(args[1..]);
			switch (args[0])
			{
				case "validate-trust":
					RequireOptions(options, "--trust");
					_ = ReadTrust(options);
					break;
				case "sign":
					Sign(options);
					break;
				case "verify":
					Verify(options);
					break;
				default:
					throw new ArgumentException($"Unknown feed signing command '{args[0]}'.");
			}
			return 0;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"Feed signing operation failed: {exception.Message}");
			return 1;
		}
	}

	private static void Sign(Dictionary<string, string> options)
	{
		RequireOptions(options, "--payload", "--trust", "--key-id", "--private-key-file", "--output", "--channel");
		UpdateFeedTrustStore trust = ReadTrust(options);
		using RSA signingKey = RSA.Create();
		char[] keyPem = File.ReadAllText(options["--private-key-file"]).ToCharArray();
		try
		{
			signingKey.ImportFromPem(keyPem);
		}
		finally
		{
			Array.Clear(keyPem);
		}
		string signed = SignedUpdateFeed.Sign(File.ReadAllText(options["--payload"]), options["--key-id"], signingKey);
		_ = SignedUpdateFeed.Verify(signed, trust, options["--channel"]);
		WriteNewFile(options["--output"], signed);
	}

	private static void Verify(Dictionary<string, string> options)
	{
		RequireOptions(options, "--feed", "--trust", "--channel", "--output");
		string payload = SignedUpdateFeed.Verify(File.ReadAllText(options["--feed"]), ReadTrust(options), options["--channel"]);
		WriteNewFile(options["--output"], payload);
	}

	private static UpdateFeedTrustStore ReadTrust(Dictionary<string, string> options)
	{
		return UpdateFeedTrustStore.Parse(File.ReadAllText(options["--trust"]));
	}

	private static void WriteNewFile(string path, string content)
	{
		using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		using StreamWriter writer = new(stream, new UTF8Encoding(false));
		writer.Write(content);
	}

	private static Dictionary<string, string> ParseOptions(string[] args)
	{
		Dictionary<string, string> options = new(StringComparer.Ordinal);
		for (int index = 0; index < args.Length; index += 2)
		{
			if (((index + 1) >= args.Length) || !args[index].StartsWith("--", StringComparison.Ordinal) ||
				string.IsNullOrWhiteSpace(args[index + 1]) || !options.TryAdd(args[index], args[index + 1]))
			{
				throw new ArgumentException($"Invalid or duplicate feed signing option at argument {index + 1}.");
			}
		}
		return options;
	}

	private static void RequireOptions(Dictionary<string, string> options, params string[] required)
	{
		if ((options.Count != required.Length) || required.Any(name => !options.ContainsKey(name)))
		{
			throw new ArgumentException($"Feed signing command requires exactly: {string.Join(", ", required)}.");
		}
	}
}
