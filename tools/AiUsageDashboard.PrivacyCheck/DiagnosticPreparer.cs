using System.Xml;

namespace AiUsageDashboard.PrivacyCheck;

public static class DiagnosticPreparer
{
	public sealed class PreparationException : Exception
	{
		public string RelativePath { get; }
		public int LineNumber { get; }
		public string Rule { get; }

		internal PreparationException(
			string relativePath,
			int lineNumber,
			string rule,
			Exception? innerException = null)
			: base($"{relativePath}:{lineNumber}: {rule}", innerException)
		{
			RelativePath = relativePath;
			LineNumber = lineNumber;
			Rule = rule;
		}
	}

	private sealed record ValidatedFile(string RelativePath, byte[] Bytes);

	private const int MaximumEntries = 10_000;
	private const int MaximumFiles = 100;
	private const int MaximumDirectoryDepth = 16;
	private const int MaximumXmlDepth = 128;
	private const int MaximumFileBytes = 64 * 1024 * 1024;
	private const long MaximumTotalBytes = 256 * 1024 * 1024;
	private const string RedactedPath = "[redacted-path]";

	public static IReadOnlyList<string> Prepare(
		string inputDirectory,
		string outputDirectory)
	{
		string inputRoot = NormalizePath(inputDirectory, "[input]");
		string outputRoot = NormalizePath(outputDirectory, "[output]");
		EnsureOutputAbsent(outputRoot);
		if (ReadAttributesIfPresent(inputRoot, "[input]") is not FileAttributes inputAttributes)
		{
			return Array.Empty<string>();
		}

		EnsureDirectory(inputAttributes, "[input]");
		EnsureNoReparseAncestors(inputRoot, "[input]");
		string outputParent = Path.GetDirectoryName(outputRoot) ??
			throw new PreparationException("[output]", 0, "output parent is required");
		EnsureNoReparseAncestors(outputParent, "[output]");

		IReadOnlyList<string> selectedPaths = SelectFiles(inputRoot);
		List<ValidatedFile> validatedFiles = new();
		long totalBytes = 0;
		foreach (string relativePath in selectedPaths)
		{
			byte[] bytes = ReadBoundedFile(inputRoot, relativePath);
			totalBytes += bytes.Length;
			if (totalBytes > MaximumTotalBytes)
			{
				throw new PreparationException(relativePath, 0, "diagnostic total exceeds 256 MiB");
			}

			ValidateContent(relativePath, bytes);
			validatedFiles.Add(new ValidatedFile(relativePath, bytes));
		}

		if (validatedFiles.Count == 0)
		{
			return Array.Empty<string>();
		}

		StageValidatedFiles(outputRoot, outputParent, validatedFiles);
		return validatedFiles.Select(file => file.RelativePath).ToArray();
	}

	private static string NormalizePath(string path, string label)
	{
		try
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new PreparationException(label, 0, "invalid diagnostic directory", exception);
		}
	}

	private static FileAttributes? ReadAttributesIfPresent(string path, string relativePath)
	{
		try
		{
			return File.GetAttributes(path);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new PreparationException(relativePath, 0, "cannot inspect diagnostic path", exception);
		}
	}

	private static void EnsureOutputAbsent(string outputRoot)
	{
		if (ReadAttributesIfPresent(outputRoot, "[output]") is not null)
		{
			throw new PreparationException("[output]", 0, "output already exists");
		}
	}

	private static void EnsureDirectory(FileAttributes attributes, string relativePath)
	{
		if ((attributes & FileAttributes.Directory) == 0)
		{
			throw new PreparationException(relativePath, 0, "diagnostic directory is required");
		}
		EnsureNotReparsePoint(attributes, relativePath);
	}

	private static void EnsureNotReparsePoint(FileAttributes attributes, string relativePath)
	{
		if ((attributes & FileAttributes.ReparsePoint) != 0)
		{
			throw new PreparationException(relativePath, 0, "reparse points are prohibited");
		}
	}

	private static void EnsureNoReparseAncestors(string path, string label)
	{
		for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
		{
			FileAttributes attributes = ReadAttributesIfPresent(ancestor, label) ??
				throw new PreparationException(label, 0, "diagnostic directory does not exist");
			EnsureDirectory(attributes, label);
		}
	}

	private static IReadOnlyList<string> SelectFiles(string inputRoot)
	{
		List<string> selectedPaths = new();
		Stack<(string Path, int Depth)> pendingDirectories = new();
		pendingDirectories.Push((inputRoot, 0));
		int entryCount = 0;
		while (pendingDirectories.TryPop(out (string Path, int Depth) directory))
		{
			string currentPath = Path.GetRelativePath(inputRoot, directory.Path).Replace('\\', '/');
			try
			{
				foreach (FileSystemInfo entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
				{
					currentPath = Path.GetRelativePath(inputRoot, entry.FullName).Replace('\\', '/');
					ValidateRelativePath(currentPath);
					if (++entryCount > MaximumEntries)
					{
						throw new PreparationException(currentPath, 0, "diagnostic entry count exceeds 10000");
					}

					EnsureNotReparsePoint(entry.Attributes, currentPath);
					if ((entry.Attributes & FileAttributes.Directory) != 0)
					{
						if (directory.Depth >= MaximumDirectoryDepth)
						{
							throw new PreparationException(currentPath, 0, "diagnostic directory depth exceeds 16");
						}
						pendingDirectories.Push((entry.FullName, directory.Depth + 1));
					}
					else if (IsSelectedPath(currentPath))
					{
						if (PrivacyRules.IsSensitiveArtifactName(entry.Name))
						{
							throw new PreparationException(currentPath, 0, "sensitive artifact filename");
						}
						selectedPaths.Add(currentPath);
						if (selectedPaths.Count > MaximumFiles)
						{
							throw new PreparationException(currentPath, 0, "diagnostic file count exceeds 100");
						}
					}
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new PreparationException(currentPath, 0, "cannot enumerate diagnostics", exception);
			}
		}

		return selectedPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
	}

	private static bool IsSelectedPath(string relativePath)
	{
		return relativePath.Equals("test-results.trx", StringComparison.OrdinalIgnoreCase) ||
			relativePath.EndsWith(".cobertura.xml", StringComparison.OrdinalIgnoreCase);
	}

	private static void ValidateRelativePath(string relativePath)
	{
		IReadOnlyList<PrivacyMatch> matches = PrivacyRules.ScanText(relativePath, includePersonalData: true);
		if ((matches.Count != 0) || relativePath.Any(char.IsControl))
		{
			throw new PreparationException(RedactedPath, 0, "unsafe diagnostic path");
		}
	}

	private static byte[] ReadBoundedFile(string inputRoot, string relativePath)
	{
		try
		{
			string path = Path.Combine(inputRoot, relativePath);
			EnsureNoReparseAncestors(Path.GetDirectoryName(path) ?? inputRoot, relativePath);
			EnsureNotReparsePoint(File.GetAttributes(path), relativePath);
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (stream.Length > MaximumFileBytes)
			{
				throw new PreparationException(relativePath, 0, "diagnostic file exceeds 64 MiB");
			}
			byte[] bytes = new byte[checked((int)stream.Length)];
			stream.ReadExactly(bytes);
			if (stream.ReadByte() != -1)
			{
				throw new PreparationException(relativePath, 0, "diagnostic file changed during read");
			}
			return bytes;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new PreparationException(relativePath, 0, "cannot read diagnostic file", exception);
		}
	}

	private static void ValidateContent(string relativePath, byte[] bytes)
	{
		if (!PrivacyRules.TryDecodeText(bytes, out string content))
		{
			throw new PreparationException(relativePath, 0, "diagnostic file is not supported text");
		}
		ScanContent(relativePath, content, 1);
		XmlReaderSettings settings = new()
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			MaxCharactersInDocument = MaximumFileBytes,
			MaxCharactersFromEntities = MaximumFileBytes
		};
		try
		{
			using MemoryStream source = new(bytes, writable: false);
			using XmlReader reader = XmlReader.Create(source, settings);
			bool hasNode = reader.Read();
			while (hasNode)
			{
				IXmlLineInfo lineInfo = (IXmlLineInfo)reader;
				if (reader.Depth > MaximumXmlDepth)
				{
					throw new PreparationException(relativePath, lineInfo.LineNumber, "diagnostic XML depth exceeds 128");
				}
				if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
					XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
				{
					int firstLineNumber = lineInfo.LineNumber;
					ScanContent(relativePath, reader.ReadContentAsString(), firstLineNumber);
					hasNode = !reader.EOF;
					continue;
				}
				if (reader.HasValue)
				{
					ScanContent(relativePath, reader.Value, lineInfo.LineNumber);
				}
				if (reader.MoveToFirstAttribute())
				{
					do
					{
						ScanContent(relativePath, reader.Name + "=" + reader.Value, lineInfo.LineNumber);
					}
					while (reader.MoveToNextAttribute());
					reader.MoveToElement();
				}
				hasNode = reader.Read();
			}
		}
		catch (XmlException exception)
		{
			throw new PreparationException(relativePath, exception.LineNumber, "invalid or prohibited diagnostic XML", exception);
		}
	}

	private static void ScanContent(string relativePath, string content, int firstLineNumber)
	{
		IReadOnlyList<PrivacyMatch> matches = PrivacyRules.ScanText(content, includePersonalData: true);
		if (matches.Count > 0)
		{
			PrivacyMatch match = matches[0];
			throw new PreparationException(relativePath, firstLineNumber + match.LineNumber - 1, match.Rule);
		}
	}

	private static void StageValidatedFiles(
		string outputRoot,
		string outputParent,
		IReadOnlyList<ValidatedFile> validatedFiles)
	{
		EnsureNoReparseAncestors(outputParent, "[output]");
		EnsureOutputAbsent(outputRoot);
		string stagingRoot = Path.Combine(outputParent, ".diagnostics-staging-" + Guid.NewGuid().ToString("N"));
		string currentPath = "[output]";
		bool ownsStagingRoot = false;
		try
		{
			Directory.CreateDirectory(stagingRoot);
			ownsStagingRoot = true;
			foreach (ValidatedFile file in validatedFiles)
			{
				currentPath = file.RelativePath;
				string destination = Path.Combine(stagingRoot, file.RelativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? stagingRoot);
				using FileStream stream = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				stream.Write(file.Bytes);
			}

			// 僅發布已驗證的同一份 bytes；移動前不會出現可上傳的 output。
			currentPath = "[output]";
			Directory.Move(stagingRoot, outputRoot);
			ownsStagingRoot = false;
		}
		catch (Exception exception)
		{
			if (ownsStagingRoot)
			{
				try
				{
					EnsureNoReparseAncestors(stagingRoot, "[staging]");
					Directory.Delete(stagingRoot, recursive: true);
				}
				catch (Exception cleanupException) when (
					cleanupException is IOException or UnauthorizedAccessException or PreparationException)
				{
					throw new PreparationException(
						"[staging]", 0, "cannot remove incomplete diagnostic staging",
						new AggregateException(exception, cleanupException));
				}
			}
			if (exception is IOException or UnauthorizedAccessException)
			{
				throw new PreparationException(currentPath, 0, "cannot stage diagnostic files", exception);
			}
			throw;
		}
	}
}
