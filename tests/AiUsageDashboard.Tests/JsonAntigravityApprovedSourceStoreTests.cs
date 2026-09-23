using AiUsageDashboard.AntigravitySpike;

namespace AiUsageDashboard.Tests;

public sealed class JsonAntigravityApprovedSourceStoreTests
{
	[Fact]
	public void SaveAndLoad_RoundTripsOfficialSource()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityApprovedSourceStore store = CreateStore(temporaryDirectory);
		AntigravityApprovedSource expected = new(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			Path.Combine(temporaryDirectory.Path, "agy.exe"));

		store.Save(expected);

		Assert.Equal(expected, store.Load());
	}

	[Fact]
	public void Save_WhenReplacingSource_PublishesOnlyNewDocument()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityApprovedSourceStore store = CreateStore(temporaryDirectory);
		store.Save(new AntigravityApprovedSource(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			Path.Combine(temporaryDirectory.Path, "old-agy.exe")));
		AntigravityApprovedSource expected = new(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			Path.Combine(temporaryDirectory.Path, "agy.exe"));

		store.Save(expected);

		Assert.Equal(expected, store.Load());
		Assert.Empty(Directory.EnumerateFiles(
			temporaryDirectory.Path,
			"*.tmp",
			SearchOption.TopDirectoryOnly));
	}

	[Theory]
	[InlineData("")]
	[InlineData("{}")]
	[InlineData("{\"schemaVersion\":2,\"sourceKind\":\"OfficialPrint\",\"path\":\"C:\\\\agy.exe\"}")]
	[InlineData("{\"schemaVersion\":1,\"sourceKind\":\"Unknown\",\"path\":\"C:\\\\agy.exe\"}")]
	[InlineData("{\"schemaVersion\":1,\"sourceKind\":\"ReviewedConPty\",\"path\":\"C:\\\\agy.profile.json\"}")]
	[InlineData("{\"schemaVersion\":1,\"sourceKind\":\"OfficialPrint\",\"path\":\"agy.exe\"}")]
	[InlineData("{\"schemaVersion\":1,\"sourceKind\":\"OfficialPrint\",\"path\":\"C:\\\\agy.exe\",\"extra\":true}")]
	public void Load_WithInvalidDocument_FailsClosed(string json)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string filePath = Path.Combine(
			temporaryDirectory.Path,
			"approved-source-v1.json");
		File.WriteAllText(filePath, json);
		JsonAntigravityApprovedSourceStore store = new(filePath);

		Assert.Throws<InvalidDataException>(() => store.Load());
	}

	[Fact]
	public void Resolve_WithStoredSource_DoesNotReadEnvironment()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityApprovedSourceStore store = CreateStore(temporaryDirectory);
		AntigravityApprovedSource expected = new(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			Path.Combine(temporaryDirectory.Path, "agy.exe"));
		store.Save(expected);
		AntigravityApprovedSourceResolver resolver = new(
			store,
			_ => throw new InvalidOperationException(
				"Process state must not be read when JSON state exists."),
			(_, _) => throw new InvalidOperationException(
				"User state must not be read when JSON state exists."));

		Assert.Equal(expected, resolver.Resolve());
	}

	[Fact]
	public void Resolve_WithLegacyUserOfficialSource_MigratesWithoutClearingEnvironment()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityApprovedSourceStore store = CreateStore(temporaryDirectory);
		string executablePath = Path.Combine(temporaryDirectory.Path, "agy.exe");
		Dictionary<string, string?> userEnvironment = new(
			StringComparer.OrdinalIgnoreCase)
		{
			[AntigravityMachineSetupEnvironmentVariables.OfficialExecutable] =
				executablePath
		};
		AntigravityApprovedSourceResolver resolver = new(
			store,
			_ => null,
			(name, target) =>
			{
				Assert.Equal(EnvironmentVariableTarget.User, target);
				return userEnvironment.GetValueOrDefault(name);
			});

		AntigravityApprovedSource resolved =
			Assert.IsType<AntigravityApprovedSource>(resolver.Resolve());

		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			resolved.SourceKind);
		Assert.Equal(executablePath, resolved.Path);
		Assert.Equal(executablePath, userEnvironment[
			AntigravityMachineSetupEnvironmentVariables.OfficialExecutable]);
		Assert.Equal(resolved, store.Load());
	}

	[Fact]
	public void Resolve_WithProcessOnlySource_DoesNotPersistOverride()
	{
		using TemporaryDirectory temporaryDirectory = new();
		JsonAntigravityApprovedSourceStore store = CreateStore(temporaryDirectory);
		string executablePath = Path.Combine(
			temporaryDirectory.Path,
			"agy.exe");
		AntigravityApprovedSourceResolver resolver = new(
			store,
			name => string.Equals(
				name,
				AntigravityMachineSetupEnvironmentVariables.OfficialExecutable,
				StringComparison.Ordinal)
					? executablePath
					: null,
			(_, target) =>
			{
				Assert.Equal(EnvironmentVariableTarget.User, target);
				return null;
			});

		AntigravityApprovedSource resolved =
			Assert.IsType<AntigravityApprovedSource>(resolver.Resolve());

		Assert.Equal(
			AntigravityMachineSetupSourceKind.OfficialPrint,
			resolved.SourceKind);
		Assert.Equal(executablePath, resolved.Path);
		Assert.Null(store.Load());
	}

	private static JsonAntigravityApprovedSourceStore CreateStore(
		TemporaryDirectory temporaryDirectory)
	{
		return new JsonAntigravityApprovedSourceStore(Path.Combine(
			temporaryDirectory.Path,
			"approved-source-v1.json"));
	}
}
