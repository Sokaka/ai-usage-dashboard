using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiUsageDashboard.Licensing;

public sealed class LegalCatalog
{
	private const int SupportedSchemaVersion = 1;
	private const int MaximumResourceBytes = 1024 * 1024;
	private const string ResourcePrefix = "AiUsageDashboard.Legal.";
	private const string ManifestPath = "third-party-notices/component-manifest.json";
	internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
	private readonly IReadOnlyList<LegalComponent> _components;
	private readonly IReadOnlyList<LegalDocument> _documents;

	public IReadOnlyList<LegalComponent> Components => _components;
	public IReadOnlyList<LegalDocument> Documents => _documents;
	public string TermsVersion { get; }
	public string Digest { get; }
	public LegalProfile Profile { get; }

	private LegalCatalog(
		LegalProfile profile,
		string termsVersion,
		IReadOnlyList<LegalComponent> components,
		IReadOnlyList<LegalDocument> documents)
	{
		Profile = profile;
		TermsVersion = termsVersion;
		_components = components;
		_documents = documents;
		Digest = CalculateDigest(termsVersion + "\n" + string.Join(
			"\n", components.OrderBy(component => component.Id, StringComparer.Ordinal)
				.Select(component => component.Id + ":" + component.Digest)));
	}

	public static LegalCatalog Load(LegalProfile profile) =>
		Load(profile, ReadResource(ManifestPath), ReadResource);

	internal static LegalCatalog Load(
		LegalProfile profile,
		byte[] manifestBytes,
		Func<string, byte[]> readDocument)
	{
		ArgumentNullException.ThrowIfNull(manifestBytes);
		ArgumentNullException.ThrowIfNull(readDocument);
		string profileName = GetProfileName(profile);
		try
		{
			using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
			JsonElement root = manifest.RootElement;
			if (root.GetProperty("schemaVersion").GetInt32() != SupportedSchemaVersion)
			{
				throw new InvalidDataException("The embedded license manifest schema is unsupported.");
			}

			string termsVersion = ReadRequiredString(root, "termsVersion");
			LegalManifestReader reader = new(root, termsVersion, readDocument);
			IReadOnlyList<LegalComponent> components = reader.ReadComponents(profileName);
			if ((components.Count == 0) || (reader.Documents.Count == 0))
			{
				throw new InvalidDataException($"License profile '{profileName}' has no components or documents.");
			}

			return new LegalCatalog(profile, termsVersion, components, reader.Documents);
		}
		catch (Exception exception) when ((exception is JsonException) ||
			(exception is KeyNotFoundException) || (exception is InvalidOperationException) ||
			(exception is ArgumentException))
		{
			throw new InvalidDataException($"Cannot read embedded license manifest for '{profileName}'.", exception);
		}
	}

	public string GetReadableText()
	{
		StringBuilder text = new();
		text.AppendLine("AI Usage Dashboard 授權與第三方條款");
		text.AppendLine("Copyright (c) 2026 Sokaka");
		text.AppendLine($"條款版本：{TermsVersion}");
		text.AppendLine($"範圍：{GetProfileName(Profile)}");
		text.AppendLine($"Acceptance digest: {Digest}");
		if (Profile == LegalProfile.Installer)
		{
			text.AppendLine("此範圍涵蓋 Updater 與即將安裝的 App ZIP；部分元件由 App ZIP 提供，並非全部包含在這個 Updater EXE。\n");
		}
		text.AppendLine("自有程式碼依 MIT；下列第三方元件各依隨附原約。接受紀錄僅保存在目前 Windows 使用者的本機。\n");
		foreach (LegalComponent component in _components)
		{
			text.AppendLine($"{component.Name} {component.Version}");
		}
		foreach (LegalDocument document in _documents.Where(document => document.IsReadable))
		{
			text.AppendLine($"\n{document.Path}\n來源：{document.Source}\n");
			text.AppendLine(document.GetText());
		}
		return text.ToString();
	}

	public void Export(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		string root = System.IO.Path.GetFullPath(directory);
		foreach (LegalDocument document in _documents)
		{
			WriteExportFile(root, document.Path, document.GetBytes());
		}
		WriteExportFile(root, ManifestPath, GetScopedManifestBytes());
		WriteExportFile(root, "ACCEPTANCE-DIGEST.txt", StrictUtf8.GetBytes(Digest + "\n"));
	}

	internal byte[] GetScopedManifestBytes()
	{
		using MemoryStream buffer = new();
		using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			writer.WriteNumber("schemaVersion", SupportedSchemaVersion);
			writer.WriteString("termsVersion", TermsVersion);
			writer.WriteStartArray("documents");
			foreach (LegalDocument document in _documents)
			{
				writer.WriteStartObject();
				writer.WriteString("id", document.Id);
				writer.WriteString("path", document.Path);
				writer.WriteString("sha256", document.Sha256);
				writer.WriteString("source", document.Source);
				writer.WriteBoolean("readable", document.IsReadable);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteStartArray("components");
			foreach (LegalComponent component in _components)
			{
				writer.WriteRawValue(component.ManifestJson);
			}
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return buffer.ToArray();
	}

	internal static string ReadRequiredString(JsonElement element, string name)
	{
		string? value = element.GetProperty(name).GetString();
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidDataException($"License metadata property '{name}' is empty.");
		}
		return value;
	}

	private static string GetProfileName(LegalProfile profile) => profile switch
	{
		LegalProfile.App => "app",
		LegalProfile.Setup => "setup",
		LegalProfile.Updater => "updater",
		LegalProfile.Capture => "capture",
		LegalProfile.Installer => "installer",
		LegalProfile.ClaudeCapture => "claude-capture",
		_ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported license profile.")
	};

	internal static string CalculateDigest(string value) =>
		Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value))).ToLowerInvariant();

	internal static LegalDocument ReadDocument(JsonElement metadata, Func<string, byte[]> readDocument)
	{
		string id = ReadRequiredString(metadata, "id");
		string path = ReadRequiredString(metadata, "path");
		ValidateRelativePath(path);
		string hash = ReadRequiredString(metadata, "sha256");
		byte[] contents = readDocument(path);
		string actualHash = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
		if (!string.Equals(hash, actualHash, StringComparison.Ordinal))
		{
			throw new InvalidDataException($"License document '{path}' does not match its manifest SHA256.");
		}
		_ = StrictUtf8.GetCharCount(contents);
		return new LegalDocument(id, path, hash, ReadRequiredString(metadata, "source"),
			metadata.GetProperty("readable").GetBoolean(), contents);
	}

	private static byte[] ReadResource(string path)
	{
		using Stream stream = typeof(LegalCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + path) ??
			throw new InvalidDataException($"Required embedded license document '{path}' is missing.");
		if ((stream.Length == 0) || (stream.Length > MaximumResourceBytes))
		{
			throw new InvalidDataException($"Embedded license document '{path}' has an invalid size {stream.Length}.");
		}
		using MemoryStream copy = new();
		stream.CopyTo(copy);
		return copy.ToArray();
	}

	private static void ValidateRelativePath(string path)
	{
		if (System.IO.Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') ||
			path.Split('/').Any(part => (part.Length == 0) || (part == ".") || (part == "..")))
		{
			throw new InvalidDataException($"License document path '{path}' is not a safe relative path.");
		}
	}

	private static void WriteExportFile(string root, string relativePath, ReadOnlySpan<byte> contents)
	{
		ValidateRelativePath(relativePath);
		string destination = System.IO.Path.Combine(root, relativePath);
		string parent = System.IO.Path.GetDirectoryName(destination) ??
			throw new InvalidDataException($"License export destination '{destination}' has no parent.");
		Directory.CreateDirectory(parent);
		if (File.Exists(destination))
		{
			if (contents.SequenceEqual(File.ReadAllBytes(destination)))
			{
				return;
			}
			throw new IOException($"Cannot export licenses: existing file '{destination}' has different contents. Choose an empty directory.");
		}
		using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		output.Write(contents);
	}
}
