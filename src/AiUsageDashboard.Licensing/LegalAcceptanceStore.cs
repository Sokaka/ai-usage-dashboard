using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace AiUsageDashboard.Licensing;

public sealed class LegalAcceptanceStore
{
	private sealed record AcceptedComponent(string Version, string Digest, DateTimeOffset AcceptedAtUtc);
	private const int ReceiptSchemaVersion = 1;
	private const int MaximumReceiptBytes = 1024 * 1024;
	private static readonly TimeSpan ReceiptLockTimeout = TimeSpan.FromSeconds(10);
	private readonly string _receiptPath;
	private readonly string _windowsSid;
	private readonly TimeProvider _timeProvider;

	internal LegalAcceptanceStore(string receiptPath, string windowsSid, TimeProvider? timeProvider = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(windowsSid);
		_receiptPath = Path.GetFullPath(receiptPath);
		_windowsSid = windowsSid;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public static LegalAcceptanceStore CreateDefault()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("AI Usage Dashboard acceptance records require a Windows user identity.");
		}
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		string sid = identity.User?.Value ??
			throw new InvalidOperationException("Cannot record license acceptance: the current Windows user has no SID.");
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localAppData))
		{
			throw new InvalidOperationException("Cannot locate the current user's LocalApplicationData directory for license acceptance.");
		}
		return new LegalAcceptanceStore(Path.Combine(localAppData, "AiUsageDashboard", "legal", "acceptance.json"), sid);
	}

	public bool IsAccepted(LegalCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		Dictionary<string, AcceptedComponent> accepted = ReadReceipt();
		return catalog.Components.All(component =>
			accepted.TryGetValue(component.Id, out AcceptedComponent? receipt) &&
			string.Equals(receipt.Version, component.Version, StringComparison.Ordinal) &&
			string.Equals(receipt.Digest, component.Digest, StringComparison.Ordinal));
	}

	public void Accept(LegalCatalog catalog, string expectedDigest)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		if (!string.Equals(catalog.Digest, expectedDigest, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Cannot accept licenses: the supplied digest does not match the currently displayed component terms. Read --licenses again.");
		}

		string mutexDigest = Convert.ToHexString(SHA256.HashData(
			LegalCatalog.StrictUtf8.GetBytes(_windowsSid + "\n" + _receiptPath.ToUpperInvariant())));
		using Mutex mutex = new(false, "Local\\AiUsageDashboard.LegalAcceptance." + mutexDigest);
		bool ownsMutex = false;
		try
		{
			try
			{
				ownsMutex = mutex.WaitOne(ReceiptLockTimeout);
			}
			catch (AbandonedMutexException)
			{
				// 前次程序可能在 rename 前後退出；持鎖後仍需完整驗證 receipt。
				ownsMutex = true;
			}
			if (!ownsMutex)
			{
				throw new TimeoutException($"Cannot save license acceptance: timed out waiting for '{_receiptPath}'.");
			}

			Dictionary<string, AcceptedComponent> accepted = ReadReceipt();
			DateTimeOffset acceptedAt = _timeProvider.GetUtcNow();
			foreach (LegalComponent component in catalog.Components)
			{
				accepted[component.Id] = new AcceptedComponent(component.Version, component.Digest, acceptedAt);
			}
			WriteReceipt(accepted);
		}
		finally
		{
			if (ownsMutex)
			{
				mutex.ReleaseMutex();
			}
		}
	}

	private Dictionary<string, AcceptedComponent> ReadReceipt()
	{
		Dictionary<string, AcceptedComponent> accepted = new(StringComparer.Ordinal);
		FileStream input;
		try
		{
			input = new FileStream(_receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
		}
		catch (FileNotFoundException)
		{
			return accepted;
		}
		catch (DirectoryNotFoundException)
		{
			return accepted;
		}
		using (input)
		{
			if ((input.Length == 0) || (input.Length > MaximumReceiptBytes))
			{
				throw new InvalidDataException($"License acceptance file '{_receiptPath}' has invalid size {input.Length}.");
			}
			try
			{
				using JsonDocument document = JsonDocument.Parse(input);
				JsonElement root = document.RootElement;
				if (root.GetProperty("schemaVersion").GetInt32() != ReceiptSchemaVersion)
				{
					throw new InvalidDataException($"License acceptance file '{_receiptPath}' uses an unsupported schema.");
				}
				if (!string.Equals(LegalCatalog.ReadRequiredString(root, "windowsSid"), _windowsSid, StringComparison.Ordinal))
				{
					return accepted;
				}
				foreach (JsonElement item in root.GetProperty("components").EnumerateArray())
				{
					string id = LegalCatalog.ReadRequiredString(item, "id");
					string digest = LegalCatalog.ReadRequiredString(item, "digest");
					if ((digest.Length != 64) || !digest.All(char.IsAsciiHexDigit))
					{
						throw new InvalidDataException($"License acceptance file '{_receiptPath}' has an invalid digest for component '{id}'.");
					}
					if (!accepted.TryAdd(id, new AcceptedComponent(
						LegalCatalog.ReadRequiredString(item, "version"), digest,
						item.GetProperty("acceptedAtUtc").GetDateTimeOffset())))
					{
						throw new InvalidDataException($"License acceptance file '{_receiptPath}' repeats component '{id}'.");
					}
				}
				return accepted;
			}
			catch (Exception exception) when ((exception is JsonException) ||
				(exception is KeyNotFoundException) || (exception is InvalidOperationException) ||
				(exception is FormatException))
			{
				throw new InvalidDataException($"Cannot read license acceptance file '{_receiptPath}'.", exception);
			}
		}
	}

	private void WriteReceipt(Dictionary<string, AcceptedComponent> accepted)
	{
		string directory = Path.GetDirectoryName(_receiptPath) ??
			throw new InvalidDataException($"License acceptance path '{_receiptPath}' has no parent directory.");
		Directory.CreateDirectory(directory);
		string temporaryPath = _receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true }))
				{
					writer.WriteStartObject();
					writer.WriteNumber("schemaVersion", ReceiptSchemaVersion);
					writer.WriteString("windowsSid", _windowsSid);
					writer.WriteStartArray("components");
					foreach ((string id, AcceptedComponent receipt) in accepted.OrderBy(item => item.Key, StringComparer.Ordinal))
					{
						writer.WriteStartObject();
						writer.WriteString("id", id);
						writer.WriteString("version", receipt.Version);
						writer.WriteString("digest", receipt.Digest);
						writer.WriteString("acceptedAtUtc", receipt.AcceptedAtUtc);
						writer.WriteEndObject();
					}
					writer.WriteEndArray();
					writer.WriteEndObject();
				}
				output.Flush(flushToDisk: true);
			}
			File.Move(temporaryPath, _receiptPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}
}
