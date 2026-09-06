using System.Text;
using System.Text.Json;

namespace AiUsageDashboard.Licensing;

internal sealed class LegalManifestReader
{
	private readonly JsonElement _root;
	private readonly string _termsVersion;
	private readonly Func<string, byte[]> _readDocument;
	private readonly Dictionary<string, JsonElement> _documentMetadata;
	private readonly Dictionary<string, LegalDocument> _documents = new(StringComparer.Ordinal);

	public IReadOnlyList<LegalDocument> Documents => _documents.Values.ToList().AsReadOnly();

	public LegalManifestReader(JsonElement root, string termsVersion, Func<string, byte[]> readDocument)
	{
		_root = root;
		_termsVersion = termsVersion;
		_readDocument = readDocument;
		_documentMetadata = root.GetProperty("documents").EnumerateArray()
			.ToDictionary(item => LegalCatalog.ReadRequiredString(item, "id"));
	}

	public IReadOnlyList<LegalComponent> ReadComponents(string profileName)
	{
		List<LegalComponent> components = [];
		HashSet<string> componentIds = new(StringComparer.Ordinal);
		foreach (JsonElement component in _root.GetProperty("components").EnumerateArray())
		{
			if (!component.GetProperty("profiles").EnumerateArray()
				.Any(item => item.GetString() == profileName))
			{
				continue;
			}
			string id = LegalCatalog.ReadRequiredString(component, "id");
			if (!componentIds.Add(id))
			{
				throw new InvalidDataException($"Duplicate license component '{id}'.");
			}
			components.Add(new LegalComponent(id,
				LegalCatalog.ReadRequiredString(component, "name"),
				LegalCatalog.ReadRequiredString(component, "version"),
				ReadComponentDigest(component, id), component.GetRawText()));
		}
		return components.AsReadOnly();
	}

	private string ReadComponentDigest(JsonElement component, string id)
	{
		StringBuilder identity = new(_termsVersion + "\n" + component.GetRawText());
		foreach (JsonElement documentReference in component.GetProperty("documents").EnumerateArray())
		{
			string documentId = documentReference.GetString() ??
				throw new InvalidDataException($"License component '{id}' has an empty document id.");
			LegalDocument document = ResolveDocument(documentId, id);
			identity.Append('\n').Append(documentId).Append(':').Append(document.Sha256);
		}
		return LegalCatalog.CalculateDigest(identity.ToString());
	}

	private LegalDocument ResolveDocument(string documentId, string componentId)
	{
		if (_documents.TryGetValue(documentId, out LegalDocument? document))
		{
			return document;
		}
		if (!_documentMetadata.TryGetValue(documentId, out JsonElement metadata))
		{
			throw new InvalidDataException($"License component '{componentId}' references missing document '{documentId}'.");
		}
		document = LegalCatalog.ReadDocument(metadata, _readDocument);
		_documents.Add(documentId, document);
		return document;
	}
}
