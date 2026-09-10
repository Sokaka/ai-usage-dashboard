using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiUsageDashboard.PrivacyCheck;

public sealed record HistoryScanOptions(string RepositoryPath, string? Revision, string? BaseRevision);

public sealed record HistoryViolation(string Commit, string Path, int LineNumber, string Rule);

public sealed record HistoryScanResult(
	string Scope,
	int Commits,
	int UniqueBlobs,
	int TextBlobs,
	int BinaryBlobs,
	long TotalBytes,
	IReadOnlyList<HistoryViolation> Violations);

public static class GitHistoryScanner
{
	private sealed record BlobLocation(string Commit, string Path);

	private const int MaximumCommits = 5000;
	private const int MaximumBlobs = 50000;
	private const int MaximumBlobBytes = 16 * 1024 * 1024;
	private const int MaximumContentBytes = 256 * 1024 * 1024;
	private const int MaximumTreeBytes = 16 * 1024 * 1024;
	private const int MaximumLocations = 250000;
	private const int MaximumViolations = 10000;
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);
	private static readonly Regex ObjectIdPattern = new(
		"\\A(?:[0-9a-f]{40}|[0-9a-f]{64})\\z",
		RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
		TimeSpan.FromSeconds(2));

	public static Task<HistoryScanResult> ScanAsync(
		HistoryScanOptions options,
		CancellationToken cancellationToken = default)
	{
		string repositoryPath = Path.GetFullPath(options.RepositoryPath);
		if (!Directory.Exists(repositoryPath))
		{
			throw new PrivacyCheckException("The history scan repository directory does not exist.");
		}
		return ScanAsync(options,
			(request, token) => GitCommand.RunAsync(repositoryPath, request, token), cancellationToken);
	}

	internal static async Task<HistoryScanResult> ScanAsync(
		HistoryScanOptions options,
		Func<GitRequest, CancellationToken, Task<byte[]>> runGit,
		CancellationToken cancellationToken)
	{
		ValidateRevisions(options);
		string discoveredRoot = StrictUtf8.GetString(await runGit(new GitRequest(
			"verify repository root", new[] { "rev-parse", "--show-toplevel" }, 32768),
			cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
		StringComparison pathComparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(discoveredRoot)),
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RepositoryPath)), pathComparison))
		{
			throw new PrivacyCheckException("Git resolved a different repository root than --repository.");
		}
		string shallow = StrictUtf8.GetString(await runGit(new GitRequest(
			"check shallow history", new[] { "rev-parse", "--is-shallow-repository" }, 128),
			cancellationToken).ConfigureAwait(false)).Trim();
		if (shallow != "false")
		{
			throw new PrivacyCheckException("History scan requires a complete, non-shallow repository.");
		}

		string scope = await ResolveScopeAsync(options, runGit, cancellationToken).ConfigureAwait(false);
		byte[] commitBytes = await runGit(new GitRequest("list commits",
			new[] { "rev-list", $"--max-count={MaximumCommits + 1}", "--topo-order", scope },
			(MaximumCommits + 1) * 66), cancellationToken).ConfigureAwait(false);
		string[] commits = StrictUtf8.GetString(commitBytes)
			.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if ((commits.Length > MaximumCommits) || commits.Any(commit => !ObjectIdPattern.IsMatch(commit)))
		{
			throw new PrivacyCheckException($"History commit inventory is invalid or exceeds {MaximumCommits} commits.");
		}

		Dictionary<string, Dictionary<string, BlobLocation>> locations = new(StringComparer.Ordinal);
		List<HistoryViolation> violations = new();
		int locationCount = 0;
		long messageContentBytes = 0;
		foreach (string commit in commits)
		{
			cancellationToken.ThrowIfCancellationRequested();
			byte[] messageBytes = await runGit(new GitRequest("read commit message",
				new[] { "show", "--no-patch", "--no-show-signature", "--format=%B", commit }, MaximumBlobBytes),
				cancellationToken).ConfigureAwait(false);
			messageContentBytes += messageBytes.Length;
			if (messageContentBytes > MaximumContentBytes)
			{
				throw new PrivacyCheckException($"History content exceeds {MaximumContentBytes} bytes.");
			}
			foreach (PrivacyMatch match in PrivacyRules.ScanText(
				StrictUtf8.GetString(messageBytes), false, cancellationToken))
			{
				AddViolation(violations, new HistoryViolation(commit, "[commit-message]", match.LineNumber, match.Rule));
			}
			byte[] tree = await runGit(new GitRequest("read commit tree",
				new[] { "ls-tree", "-r", "-z", "--full-tree", commit }, MaximumTreeBytes),
				cancellationToken).ConfigureAwait(false);
			locationCount += AddTreeLocations(commit, tree, locations);
			if ((locations.Count > MaximumBlobs) || (locationCount > MaximumLocations))
			{
				throw new PrivacyCheckException(
					$"History inventory exceeds {MaximumBlobs} blobs or {MaximumLocations} distinct blob paths.");
			}
		}

		if (locations.Count == 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return new HistoryScanResult(scope, commits.Length, 0, 0, 0, messageContentBytes, violations);
		}
		string[] blobIds = locations.Keys.Order(StringComparer.Ordinal).ToArray();
		byte[] batch = await runGit(new GitRequest("read historical blobs",
			new[] { "cat-file", "--batch" }, MaximumContentBytes + (MaximumBlobs * 100),
			string.Join('\n', blobIds) + "\n"), cancellationToken).ConfigureAwait(false);
		return InspectBatch(scope, commits.Length, blobIds, batch, locations, violations,
			messageContentBytes, cancellationToken);
	}

	private static void ValidateRevisions(HistoryScanOptions options)
	{
		if ((options.Revision is not null) && (options.Revision != "HEAD") &&
			!ObjectIdPattern.IsMatch(options.Revision))
		{
			throw new PrivacyCheckException("History revision must be HEAD or a complete Git object ID.");
		}
		if ((options.BaseRevision is not null) &&
			((options.Revision is null) || !ObjectIdPattern.IsMatch(options.BaseRevision)))
		{
			throw new PrivacyCheckException("History base requires a revision and a complete Git object ID.");
		}
	}

	private static async Task<string> ResolveScopeAsync(
		HistoryScanOptions options,
		Func<GitRequest, CancellationToken, Task<byte[]>> runGit,
		CancellationToken cancellationToken)
	{
		if (options.Revision is null)
		{
			return "--all";
		}
		string revision = await ResolveCommitAsync(options.Revision, runGit, cancellationToken).ConfigureAwait(false);
		if (options.BaseRevision is null)
		{
			return revision;
		}
		string baseRevision = await ResolveCommitAsync(options.BaseRevision, runGit, cancellationToken).ConfigureAwait(false);
		return $"{baseRevision}..{revision}";
	}

	private static async Task<string> ResolveCommitAsync(
		string revision,
		Func<GitRequest, CancellationToken, Task<byte[]>> runGit,
		CancellationToken cancellationToken)
	{
		byte[] bytes = await runGit(new GitRequest("resolve commit",
			new[] { "rev-parse", "--verify", $"{revision}^{{commit}}" }, 128),
			cancellationToken).ConfigureAwait(false);
		string commit = StrictUtf8.GetString(bytes).Trim();
		if (!ObjectIdPattern.IsMatch(commit))
		{
			throw new PrivacyCheckException("Git returned an invalid resolved commit ID.");
		}
		return commit;
	}

	private static int AddTreeLocations(
		string commit,
		byte[] tree,
		Dictionary<string, Dictionary<string, BlobLocation>> locations)
	{
		if ((tree.Length > 0) && (tree[^1] != 0))
		{
			throw new PrivacyCheckException($"Commit {commit} returned an incomplete tree inventory.");
		}
		int added = 0;
		foreach (string entry in StrictUtf8.GetString(tree).Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			int separator = entry.IndexOf('\t');
			if (separator < 0)
			{
				throw new PrivacyCheckException($"Commit {commit} returned an invalid tree entry.");
			}
			string[] metadata = entry[..separator].Split(' ');
			if ((metadata.Length != 3) || (metadata[1] != "blob") || !ObjectIdPattern.IsMatch(metadata[2]))
			{
				throw new PrivacyCheckException(
					$"Commit {commit} contains an unsupported tree entry; submodule contents cannot be verified.");
			}
			string path = entry[(separator + 1)..];
			if (string.IsNullOrEmpty(path))
			{
				throw new PrivacyCheckException($"Commit {commit} contains an empty tree path.");
			}
			if (!locations.TryGetValue(metadata[2], out Dictionary<string, BlobLocation>? blobLocations))
			{
				blobLocations = new Dictionary<string, BlobLocation>(StringComparer.Ordinal);
				locations.Add(metadata[2], blobLocations);
			}
			if (blobLocations.TryAdd(path, new BlobLocation(commit, path)))
			{
				added++;
			}
		}
		return added;
	}

	private static HistoryScanResult InspectBatch(
		string scope,
		int commitCount,
		string[] blobIds,
		byte[] batch,
		Dictionary<string, Dictionary<string, BlobLocation>> locations,
		List<HistoryViolation> violations,
		long messageContentBytes,
		CancellationToken cancellationToken)
	{
		int offset = 0;
		int textBlobs = 0;
		long contentBytes = messageContentBytes;
		foreach (string blobId in blobIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			byte[] bytes = ReadBlob(blobId, batch, ref offset);
			contentBytes += bytes.Length;
			if (contentBytes > MaximumContentBytes)
			{
				throw new PrivacyCheckException($"History content exceeds {MaximumContentBytes} bytes.");
			}
			bool isText = PrivacyRules.TryDecodeText(bytes, out string content);
			IReadOnlyList<PrivacyMatch> matches = isText
				? PrivacyRules.ScanText(content, false, cancellationToken) : Array.Empty<PrivacyMatch>();
			if (isText)
			{
				textBlobs++;
			}
			foreach (BlobLocation location in locations[blobId].Values)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string displayPath = GetSafePath(location.Path);
				foreach (PrivacyMatch pathMatch in PrivacyRules.ScanText(location.Path, false, cancellationToken))
				{
					AddViolation(violations, new HistoryViolation(location.Commit, displayPath, 0, $"path: {pathMatch.Rule}"));
				}
				string fileName = location.Path.Split('/')[^1];
				if (PrivacyRules.IsSensitiveArtifactName(fileName))
				{
					AddViolation(violations, new HistoryViolation(location.Commit, displayPath, 0, "sensitive artifact filename"));
				}
				if (!isText && PrivacyRules.IsKnownTextFile(fileName))
				{
					AddViolation(violations, new HistoryViolation(location.Commit, displayPath, 0, "invalid text encoding"));
				}
				foreach (PrivacyMatch match in matches)
				{
					AddViolation(violations, new HistoryViolation(location.Commit, displayPath, match.LineNumber, match.Rule));
				}
			}
		}
		if (offset != batch.Length)
		{
			throw new PrivacyCheckException("Historical blob response contains unaccounted bytes.");
		}
		cancellationToken.ThrowIfCancellationRequested();
		return new HistoryScanResult(scope, commitCount, blobIds.Length, textBlobs,
			blobIds.Length - textBlobs, contentBytes, violations);
	}

	private static byte[] ReadBlob(string blobId, byte[] batch, ref int offset)
	{
		int headerEnd = Array.IndexOf(batch, (byte)'\n', offset);
		if ((headerEnd < offset) || ((headerEnd - offset) > 128))
		{
			throw new PrivacyCheckException($"Historical blob {blobId} has an incomplete header.");
		}
		string[] header = Encoding.ASCII.GetString(batch, offset, headerEnd - offset).Split(' ');
		if ((header.Length != 3) || (header[0] != blobId) || (header[1] != "blob") ||
			!int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
			(length < 0) || (length > MaximumBlobBytes))
		{
			throw new PrivacyCheckException($"Historical blob {blobId} has an invalid header or exceeds {MaximumBlobBytes} bytes.");
		}
		offset = headerEnd + 1;
		if ((length >= (batch.Length - offset)) || (batch[offset + length] != (byte)'\n'))
		{
			throw new PrivacyCheckException($"Historical blob {blobId} has incomplete content.");
		}
		byte[] content = batch.AsSpan(offset, length).ToArray();
		offset += length + 1;
		return content;
	}

	private static string GetSafePath(string path)
	{
		return (path.Any(char.IsControl) || (PrivacyRules.ScanText(path, true).Count > 0))
			? "[redacted-path]" : path;
	}

	private static void AddViolation(List<HistoryViolation> violations, HistoryViolation violation)
	{
		if (violations.Count >= MaximumViolations)
		{
			throw new PrivacyCheckException($"History findings exceed {MaximumViolations}; the scan is incomplete.");
		}
		violations.Add(violation);
	}
}
