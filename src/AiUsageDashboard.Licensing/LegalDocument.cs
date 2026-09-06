namespace AiUsageDashboard.Licensing;

public sealed class LegalDocument
{
	private readonly byte[] _contents;

	public string Id { get; }
	public string Path { get; }
	public string Sha256 { get; }
	public string Source { get; }
	public bool IsReadable { get; }

	internal LegalDocument(
		string id,
		string path,
		string sha256,
		string source,
		bool isReadable,
		byte[] contents)
	{
		Id = id;
		Path = path;
		Sha256 = sha256;
		Source = source;
		IsReadable = isReadable;
		_contents = contents;
	}

	public string GetText() => LegalCatalog.StrictUtf8.GetString(_contents);

	internal ReadOnlySpan<byte> GetBytes() => _contents;
}
