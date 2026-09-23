using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageDashboard.AntigravitySpike;

public sealed record AntigravityApprovedSource
{
	public AntigravityMachineSetupSourceKind SourceKind { get; }

	public string Path { get; }

	public AntigravityApprovedSource(
		AntigravityMachineSetupSourceKind sourceKind,
		string path)
	{
		if (sourceKind != AntigravityMachineSetupSourceKind.OfficialPrint)
		{
			throw new ArgumentOutOfRangeException(nameof(sourceKind));
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!System.IO.Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
		{
			throw new ArgumentException(
				"The approved AGY source path must be fully qualified.",
				nameof(path));
		}

		SourceKind = sourceKind;
		Path = System.IO.Path.GetFullPath(path);
	}
}

public interface IAntigravityApprovedSourceStore
{
	AntigravityApprovedSource? Load();

	void Save(AntigravityApprovedSource source);

	bool TrySaveIfMissing(AntigravityApprovedSource source);

	void Clear();
}

public static class AntigravityApprovedSourcePaths
{
	private const string ApplicationDirectoryName = "AiUsageDashboard";
	private const string AntigravityDirectoryName = "antigravity";
	private const string ApprovedSourceFileName = "approved-source-v1.json";

	public static string GetDefaultFilePath()
	{
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localApplicationData))
		{
			throw new InvalidOperationException(
				"The local application-data directory is unavailable.");
		}

		return System.IO.Path.Combine(
			localApplicationData,
			ApplicationDirectoryName,
			AntigravityDirectoryName,
			ApprovedSourceFileName);
	}
}

public sealed class JsonAntigravityApprovedSourceStore :
	IAntigravityApprovedSourceStore
{
	private sealed record ApprovedSourceDocument(
		int SchemaVersion,
		string SourceKind,
		string Path);

	private const int CurrentSchemaVersion = 1;
	private const int MaximumDocumentSizeBytes = 64 * 1024;
	private const string ManagedPathDescription =
		"The approved AGY source path";
	private static readonly HashSet<string> DocumentPropertyNames = new(
		new[]
		{
			"path",
			"schemaVersion",
			"sourceKind"
		},
		StringComparer.Ordinal);
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		MaxDepth = 4,
		PropertyNameCaseInsensitive = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		WriteIndented = true
	};
	private readonly string _filePath;

	public JsonAntigravityApprovedSourceStore(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		_filePath = System.IO.Path.GetFullPath(filePath);
		if (string.IsNullOrWhiteSpace(System.IO.Path.GetDirectoryName(_filePath)))
		{
			throw new ArgumentException(
				"The approved AGY source path has no directory.",
				nameof(filePath));
		}
	}

	public static JsonAntigravityApprovedSourceStore CreateDefault()
	{
		return new JsonAntigravityApprovedSourceStore(
			AntigravityApprovedSourcePaths.GetDefaultFilePath());
	}

	public AntigravityApprovedSource? Load()
	{
		FileStream stream;

		try
		{
			AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				_filePath,
				ManagedPathDescription);
			stream = new FileStream(
				_filePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete,
				bufferSize: 4096,
				FileOptions.SequentialScan);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}

		using (stream)
		{
			if (stream.Length is <= 0 or > MaximumDocumentSizeBytes)
			{
				throw new InvalidDataException(
					"The approved AGY source document has an invalid size.");
			}

			byte[] contents = GC.AllocateUninitializedArray<byte>(
				checked((int)stream.Length));
			stream.ReadExactly(contents);
			return ParseDocument(contents);
		}
	}

	public void Save(AntigravityApprovedSource source)
	{
		ArgumentNullException.ThrowIfNull(source);
		Write(source, overwrite: true);
	}

	public bool TrySaveIfMissing(AntigravityApprovedSource source)
	{
		ArgumentNullException.ThrowIfNull(source);
		return Write(source, overwrite: false);
	}

	public void Clear()
	{
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			_filePath,
			ManagedPathDescription);
		File.Delete(_filePath);
	}

	private bool Write(AntigravityApprovedSource source, bool overwrite)
	{
		byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
			new ApprovedSourceDocument(
				CurrentSchemaVersion,
				source.SourceKind.ToString(),
				source.Path),
			SerializerOptions);
		if (contents.Length is <= 0 or > MaximumDocumentSizeBytes)
		{
			throw new InvalidOperationException(
				"The approved AGY source document exceeds its size limit.");
		}

		string directoryPath = System.IO.Path.GetDirectoryName(_filePath)!;
		AntigravityManagedFilePathGuard.CreateDirectoryAndEnsureSafe(
			directoryPath,
			ManagedPathDescription);
		AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
			_filePath,
			ManagedPathDescription);
		string temporaryFilePath = System.IO.Path.Combine(
			directoryPath,
			$".{System.IO.Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

		try
		{
			AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryFilePath,
				ManagedPathDescription);
			using (FileStream stream = new(
				temporaryFilePath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				FileOptions.WriteThrough))
			{
				stream.Write(contents);
				stream.Flush(flushToDisk: true);
			}

			AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				_filePath,
				ManagedPathDescription);
			if (overwrite)
			{
				File.Move(temporaryFilePath, _filePath, overwrite: true);
				return true;
			}

			try
			{
				File.Move(temporaryFilePath, _filePath, overwrite: false);
				return true;
			}
			catch (IOException) when (File.Exists(_filePath))
			{
				AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
					_filePath,
					ManagedPathDescription);
				return false;
			}
		}
		finally
		{
			TryDeleteTemporaryFile(temporaryFilePath);
		}
	}

	private static AntigravityApprovedSource ParseDocument(byte[] contents)
	{
		try
		{
			using JsonDocument parsed = JsonDocument.Parse(
				contents,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 4
				});
			if (!HasExactProperties(
					parsed.RootElement,
					DocumentPropertyNames))
			{
				throw new InvalidDataException(
					"The approved AGY source document has an invalid structure.");
			}

			ApprovedSourceDocument? document =
				JsonSerializer.Deserialize<ApprovedSourceDocument>(
					contents,
					SerializerOptions);
			if ((document is null) ||
				(document.SchemaVersion != CurrentSchemaVersion) ||
				!Enum.TryParse(
					document.SourceKind,
					ignoreCase: false,
					out AntigravityMachineSetupSourceKind sourceKind) ||
				(sourceKind !=
					AntigravityMachineSetupSourceKind.OfficialPrint) ||
				!string.Equals(
					document.SourceKind,
					sourceKind.ToString(),
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The approved AGY source document has an unsupported value.");
			}

			return new AntigravityApprovedSource(sourceKind, document.Path);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				"The approved AGY source document is malformed.",
				exception);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidDataException(
				"The approved AGY source document contains an invalid path.",
				exception);
		}
	}

	private static bool HasExactProperties(
		JsonElement element,
		IReadOnlySet<string> expectedPropertyNames)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		HashSet<string> observedPropertyNames = new(StringComparer.Ordinal);
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (!observedPropertyNames.Add(property.Name))
			{
				return false;
			}
		}

		return observedPropertyNames.SetEquals(expectedPropertyNames);
	}

	private static void TryDeleteTemporaryFile(string temporaryFilePath)
	{
		try
		{
			AntigravityManagedFilePathGuard.EnsureExistingFilePathIsSafe(
				temporaryFilePath,
				ManagedPathDescription);
			File.Delete(temporaryFilePath);
		}
		catch
		{
			// Unique temporary files are never accepted as approved source state.
		}
	}
}

public sealed class AntigravityApprovedSourceResolver
{
	private readonly Func<string, string?> _getProcessEnvironmentVariable;
	private readonly Func<string, EnvironmentVariableTarget, string?>
		_getEnvironmentVariable;
	private readonly IAntigravityApprovedSourceStore _store;

	public AntigravityApprovedSourceResolver(
		IAntigravityApprovedSourceStore store,
		Func<string, string?> getProcessEnvironmentVariable,
		Func<string, EnvironmentVariableTarget, string?> getEnvironmentVariable)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_getProcessEnvironmentVariable = getProcessEnvironmentVariable ??
			throw new ArgumentNullException(
				nameof(getProcessEnvironmentVariable));
		_getEnvironmentVariable = getEnvironmentVariable ??
			throw new ArgumentNullException(nameof(getEnvironmentVariable));
	}

	public AntigravityApprovedSource? Resolve()
	{
		AntigravityApprovedSource? stored = _store.Load();
		if (stored is not null)
		{
			return stored;
		}

		string? userOfficialExecutable = _getEnvironmentVariable(
			AntigravityMachineSetupEnvironmentVariables.OfficialExecutable,
			EnvironmentVariableTarget.User);
		if (!string.IsNullOrWhiteSpace(userOfficialExecutable))
		{
			return MigrateUserSource(new AntigravityApprovedSource(
				AntigravityMachineSetupSourceKind.OfficialPrint,
				userOfficialExecutable));
		}

		string? processOfficialExecutable = _getProcessEnvironmentVariable(
			AntigravityMachineSetupEnvironmentVariables.OfficialExecutable);
		if (!string.IsNullOrWhiteSpace(processOfficialExecutable))
		{
			return new AntigravityApprovedSource(
				AntigravityMachineSetupSourceKind.OfficialPrint,
				processOfficialExecutable);
		}

		return null;
	}

	private AntigravityApprovedSource MigrateUserSource(
		AntigravityApprovedSource source)
	{
		_store.TrySaveIfMissing(source);
		return _store.Load() ??
			throw new IOException(
				"The migrated AGY source state could not be loaded.");
	}
}
