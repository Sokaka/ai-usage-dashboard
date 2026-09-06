namespace AiUsageDashboard.Licensing;

public sealed class LegalComponent
{
	public string Id { get; }
	public string Name { get; }
	public string Version { get; }
	public string Digest { get; }
	internal string ManifestJson { get; }

	internal LegalComponent(
		string id,
		string name,
		string version,
		string digest,
		string manifestJson)
	{
		Id = id;
		Name = name;
		Version = version;
		Digest = digest;
		ManifestJson = manifestJson;
	}
}
