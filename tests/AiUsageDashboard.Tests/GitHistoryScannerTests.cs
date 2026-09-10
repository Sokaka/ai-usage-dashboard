using System.Text;
using System.Text.Json;

using AiUsageDashboard.PrivacyCheck;

namespace AiUsageDashboard.Tests;

public sealed class GitHistoryScannerTests
{
	private sealed class GitFixture
	{
		public readonly List<GitRequest> Requests = new();
		public readonly Dictionary<string, string> Trees = new(StringComparer.Ordinal);
		public readonly Dictionary<string, byte[]> Blobs = new(StringComparer.Ordinal);
		public readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal);

		public string Root { get; } = Path.GetFullPath(Path.GetTempPath());
		public string CommitInventory { get; set; } = $"{CurrentCommit}\n{PreviousCommit}\n";
		public bool IsShallow { get; set; }
		public bool HasTrailingBatchBytes { get; set; }
		public bool TruncateBatch { get; set; }
		public Action? OnBatchRead { get; set; }

		public Task<byte[]> RunAsync(GitRequest request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			string? output = request.Operation switch
			{
				"verify repository root" => Root + "\n",
				"check shallow history" => IsShallow ? "true\n" : "false\n",
				"resolve commit" => request.Arguments[^1].StartsWith(PreviousCommit, StringComparison.Ordinal)
					? PreviousCommit + "\n" : CurrentCommit + "\n",
				"list commits" => CommitInventory,
				"read commit message" => Messages.GetValueOrDefault(request.Arguments[^1], "fixture commit\n"),
				"read commit tree" => Trees.GetValueOrDefault(request.Arguments[^1], string.Empty),
				"read historical blobs" => null,
				_ => throw new InvalidOperationException($"Unexpected fixture Git operation: {request.Operation}")
			};
			if (output is not null)
			{
				return Task.FromResult(Encoding.UTF8.GetBytes(output));
			}
			using MemoryStream batch = new();
			Assert.NotNull(request.StandardInput);
			foreach (string objectId in request.StandardInput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				byte[] blob = Blobs[objectId];
				batch.Write(Encoding.ASCII.GetBytes($"{objectId} blob {blob.Length}\n"));
				batch.Write(blob);
				batch.WriteByte((byte)'\n');
			}
			if (HasTrailingBatchBytes)
			{
				batch.WriteByte((byte)'x');
			}
			byte[] response = batch.ToArray();
			OnBatchRead?.Invoke();
			return Task.FromResult(TruncateBatch ? response[..^1] : response);
		}

		public Task<HistoryScanResult> ScanAsync(string? revision = "HEAD", string? baseRevision = null,
			CancellationToken cancellationToken = default)
		{
			return GitHistoryScanner.ScanAsync(new HistoryScanOptions(Root, revision, baseRevision),
				RunAsync, cancellationToken);
		}
	}

	private const string CurrentCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	private const string PreviousCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
	private const string BlobId = "cccccccccccccccccccccccccccccccccccccccc";

	[Fact]
	public async Task History_FindsSecretInDocumentRemovedByLaterCommitWithoutReturningItsValue()
	{
		GitFixture fixture = new();
		string secret = string.Concat("gh", "p_", new string('A', 36));
		fixture.Trees[PreviousCommit] = $"100644 blob {BlobId}\tdocs/deleted.md\0";
		fixture.Blobs[BlobId] = Encoding.UTF8.GetBytes($"# Old documentation\n{secret}\n");

		HistoryScanResult result = await fixture.ScanAsync();

		Assert.Equal(2, result.Commits);
		HistoryViolation violation = Assert.Single(result.Violations);
		Assert.Equal(PreviousCommit, violation.Commit);
		Assert.Equal("docs/deleted.md", violation.Path);
		Assert.Equal(2, violation.LineNumber);
		Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
	}

	[Fact]
	public async Task History_ChecksEveryFilenameEvenWhenBlobContentIsShared()
	{
		GitFixture fixture = new();
		fixture.Trees[CurrentCommit] =
			$"100644 blob {BlobId}\tREADME.md\0100644 blob {BlobId}\t.env.production\0";
		fixture.Blobs[BlobId] = Encoding.UTF8.GetBytes("safe fixture content");

		HistoryScanResult result = await fixture.ScanAsync();

		Assert.Equal(1, result.UniqueBlobs);
		Assert.Equal(".env.production", Assert.Single(result.Violations).Path);
		Assert.Equal(BlobId + "\n", Assert.Single(fixture.Requests,
			request => request.Operation == "read historical blobs").StandardInput);
	}

	[Fact]
	public async Task History_ScansUnknownTextExtensionAndCommitMessages()
	{
		GitFixture fixture = new();
		string secret = string.Concat("gh", "p_", new string('B', 36));
		fixture.Messages[CurrentCommit] = secret;
		fixture.Trees[CurrentCommit] = $"100644 blob {BlobId}\tnew-format.custom\0";
		fixture.Blobs[BlobId] = Encoding.UTF8.GetBytes(secret);

		HistoryScanResult result = await fixture.ScanAsync();

		Assert.Equal(2, result.Violations.Count);
		Assert.Contains(result.Violations, violation => violation.Path == "[commit-message]");
		Assert.Contains(result.Violations, violation => violation.Path == "new-format.custom");
	}

	[Fact]
	public async Task History_RejectsSecretInFilenameAndRedactsIt()
	{
		GitFixture fixture = new();
		string secret = string.Concat("gh", "p_", new string('C', 36));
		fixture.Trees[CurrentCommit] = $"100644 blob {BlobId}\tdocs/{secret}.md\0";
		fixture.Blobs[BlobId] = Encoding.UTF8.GetBytes("safe");

		HistoryScanResult result = await fixture.ScanAsync();

		Assert.Equal("[redacted-path]", Assert.Single(result.Violations).Path);
		Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
	}

	[Fact]
	public async Task History_ReportsBinaryCountButRejectsInvalidKnownText()
	{
		GitFixture fixture = new();
		fixture.Trees[CurrentCommit] =
			$"100644 blob {BlobId}\timage.png\0100644 blob {BlobId}\tcorrupt.md\0";
		fixture.Blobs[BlobId] = new byte[] { 0xff, 0, 1, 2, 3 };

		HistoryScanResult result = await fixture.ScanAsync();

		Assert.Equal(1, result.BinaryBlobs);
		Assert.Equal(0, result.TextBlobs);
		Assert.Equal("corrupt.md", Assert.Single(result.Violations).Path);
	}

	[Fact]
	public async Task History_RejectsShallowInventory()
	{
		GitFixture fixture = new() { IsShallow = true };

		await Assert.ThrowsAsync<PrivacyCheckException>(() => fixture.ScanAsync());

		Assert.DoesNotContain(fixture.Requests, request => request.Operation == "list commits");
	}

	[Fact]
	public async Task History_UsesExplicitCommitRangeAndAllReferencesScope()
	{
		GitFixture incremental = new() { CommitInventory = CurrentCommit + "\n" };
		HistoryScanResult rangeResult = await incremental.ScanAsync(baseRevision: PreviousCommit);
		Assert.Equal($"{PreviousCommit}..{CurrentCommit}", rangeResult.Scope);
		Assert.Equal(rangeResult.Scope, Assert.Single(incremental.Requests,
			request => request.Operation == "list commits").Arguments[^1]);

		GitFixture complete = new();
		HistoryScanResult allResult = await complete.ScanAsync(revision: null);
		Assert.Equal("--all", allResult.Scope);
	}

	[Fact]
	public async Task History_FailsOnIncompleteOrUnsupportedTrees()
	{
		GitFixture incomplete = new();
		incomplete.Trees[CurrentCommit] = $"100644 blob {BlobId}\tfile.md";
		await Assert.ThrowsAsync<PrivacyCheckException>(() => incomplete.ScanAsync());

		GitFixture submodule = new();
		submodule.Trees[CurrentCommit] = $"160000 commit {BlobId}\texternal\0";
		await Assert.ThrowsAsync<PrivacyCheckException>(() => submodule.ScanAsync());
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task History_FailsOnTruncatedOrExtraBatchBytes(bool truncate, bool append)
	{
		GitFixture fixture = new() { TruncateBatch = truncate, HasTrailingBatchBytes = append };
		fixture.Trees[CurrentCommit] = $"100644 blob {BlobId}\tfile.md\0";
		fixture.Blobs[BlobId] = Encoding.UTF8.GetBytes("safe");

		await Assert.ThrowsAsync<PrivacyCheckException>(() => fixture.ScanAsync());
	}

	[Fact]
	public async Task History_FailsWhenCommitLimitIsReachedInsteadOfReportingPartialSuccess()
	{
		GitFixture fixture = new()
		{
			CommitInventory = string.Concat(Enumerable.Repeat(CurrentCommit + "\n", 5001))
		};

		await Assert.ThrowsAsync<PrivacyCheckException>(() => fixture.ScanAsync());

		Assert.DoesNotContain(fixture.Requests, request => request.Operation == "read commit tree");
	}

	[Fact]
	public async Task History_ObservesCancellationAfterGitHasReturnedBlobContent()
	{
		using CancellationTokenSource cancellation = new();
		GitFixture fixture = new() { OnBatchRead = cancellation.Cancel };
		fixture.Trees[CurrentCommit] = $"100644 blob {BlobId}\tfile.md\0";
		fixture.Blobs[BlobId] = Encoding.UTF8.GetBytes("safe");

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			fixture.ScanAsync(cancellationToken: cancellation.Token));
	}

	[Fact]
	public async Task History_RejectsRepositoryRootMismatch()
	{
		GitFixture fixture = new();
		HistoryScanOptions options = new(Path.Combine(fixture.Root, "different-root"), "HEAD", null);

		await Assert.ThrowsAsync<PrivacyCheckException>(() =>
			GitHistoryScanner.ScanAsync(options, fixture.RunAsync, CancellationToken.None));

		Assert.Single(fixture.Requests);
	}

	[Fact]
	public async Task History_FailsWhenBlobAliasesMultiplyFindingsPastTheLimit()
	{
		GitFixture fixture = new();
		string secret = string.Concat("gh", "p_", new string('D', 36));
		fixture.Trees[CurrentCommit] =
			$"100644 blob {BlobId}\tone.md\0100644 blob {BlobId}\ttwo.md\0";
		fixture.Blobs[BlobId] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(secret + "\n", 6000)));

		PrivacyCheckException exception = await Assert.ThrowsAsync<PrivacyCheckException>(() => fixture.ScanAsync());

		Assert.Contains("10000", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void GitEnvironment_RemovesInheritedRepositorySelectorsWithoutChangingProcessEnvironment()
	{
		Dictionary<string, string?> environment = new()
		{
			["GIT_DIR"] = "some-other-repository",
			["GIT_WORK_TREE"] = "some-other-worktree",
			["GIT_NAMESPACE"] = "hidden-scope",
			["GIT_SHALLOW_FILE"] = "incomplete-history",
			["GIT_OBJECT_DIRECTORY"] = "other-objects",
			["GIT_CONFIG_COUNT"] = "1",
			["PATH"] = "preserved-path"
		};

		GitCommand.ConfigureEnvironment(environment);

		Assert.False(environment.ContainsKey("GIT_DIR"));
		Assert.False(environment.ContainsKey("GIT_WORK_TREE"));
		Assert.False(environment.ContainsKey("GIT_NAMESPACE"));
		Assert.False(environment.ContainsKey("GIT_SHALLOW_FILE"));
		Assert.False(environment.ContainsKey("GIT_OBJECT_DIRECTORY"));
		Assert.False(environment.ContainsKey("GIT_CONFIG_COUNT"));
		Assert.Equal("0", environment["GIT_OPTIONAL_LOCKS"]);
		Assert.Equal("1", environment["GIT_NO_LAZY_FETCH"]);
		Assert.Equal("preserved-path", environment["PATH"]);
	}
}
