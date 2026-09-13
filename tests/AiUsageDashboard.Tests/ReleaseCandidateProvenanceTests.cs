using System.Diagnostics;
using System.Text.Json;

namespace AiUsageDashboard.Tests;

public sealed class ReleaseCandidateProvenanceTests
{
	private sealed record ReleaseApiResponse(
		string? Failure = null,
		Dictionary<string, object?>? Overrides = null,
		Dictionary<string, object?>? AssetOverrides = null,
		bool OmitAsset = false);

	private sealed record FixtureRequest(
		int BuildAttempt,
		int PublishAttempt,
		Dictionary<string, string> EnvironmentOverrides,
		Dictionary<string, object?> ArtifactOverrides,
		string BuildArtifactId = ArtifactId,
		string? ArtifactReadFailure = null,
		ReleaseApiResponse? ReleaseView = null,
		ReleaseApiResponse? FreezeRead = null);

	private sealed record FixtureResult(
		string Phase,
		string? Error,
		int ArtifactReadCount,
		int RemoteMutationCount,
		int ReleaseViewReadCount,
		string[] ReleaseApiPaths,
		string[] BuildOutputs,
		string[] ProvenanceOutputs,
		string? Notes,
		JsonElement Receipt);

	private const string RunId = "12345";
	private const string ArtifactId = "98765";
	private const long ReleaseId = 3456;
	private const string SourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	private const string ArtifactDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

	[Fact]
	public void Workflow_ConnectsSavedBuildIdentityToValidatedArtifactDownload()
	{
		string workflow = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root, ".github", "workflows", "internal-release-candidate.yml"));
		foreach ((string output, string step, string stepOutput, string environment) in new[]
		{
			("build_run_id", "release_source", "build_run_id", "BUILD_RUN_ID"),
			("build_run_attempt", "release_source", "build_run_attempt", "BUILD_RUN_ATTEMPT"),
			("artifact_id", "candidate_upload", "artifact-id", "CANDIDATE_ARTIFACT_ID"),
			("artifact_digest", "candidate_upload", "artifact-digest", "CANDIDATE_ARTIFACT_DIGEST")
		})
		{
			Assert.Contains($"{output}: ${{{{ steps.{step}.outputs.{stepOutput} }}}}", workflow);
			Assert.Contains($"{environment}: ${{{{ needs.build-candidate.outputs.{output} }}}}", workflow);
		}

		int validationIndex = workflow.IndexOf("      - name: Validate candidate provenance", StringComparison.Ordinal);
		int downloadIndex = workflow.IndexOf("      - name: Download verified candidate", StringComparison.Ordinal);
		int publishIndex = workflow.IndexOf("      - name: Reverify and publish private prerelease", StringComparison.Ordinal);
		Assert.True(validationIndex >= 0);
		Assert.True(downloadIndex > validationIndex);
		Assert.True(publishIndex > downloadIndex);
		string download = workflow[downloadIndex..publishIndex];
		Assert.Contains("artifact-ids: ${{ steps.candidate_provenance.outputs.artifact_id }}", download);
		Assert.Contains("run-id: ${{ needs.build-candidate.outputs.build_run_id }}", download);
		Assert.Contains("digest-mismatch: error", download);
		Assert.DoesNotContain("          name:", download);
		Assert.DoesNotContain("gh run download", workflow);
	}

	[Theory]
	[InlineData(1, 2, ArtifactId)]
	[InlineData(3, 3, "98767")]
	public async Task Workflow_FreezesDraftWithoutTagRefAndPreservesOriginalBuildIdentity(
		int buildAttempt,
		int publishAttempt,
		string buildArtifactId)
	{
		FixtureResult result = await RunFixtureAsync(new(
			buildAttempt,
			publishAttempt,
			new(),
			new(),
			buildArtifactId));

		Assert.Null(result.Error);
		Assert.Equal("complete", result.Phase);
		Assert.Equal(1, result.ArtifactReadCount);
		Assert.Equal(1, result.RemoteMutationCount);
		Assert.Equal(1, result.ReleaseViewReadCount);
		Assert.Equal([$"repos/fixture/release-candidate/releases/{ReleaseId}"], result.ReleaseApiPaths);
		Assert.Contains($"build_run_id={RunId}", result.BuildOutputs);
		Assert.Contains($"build_run_attempt={buildAttempt}", result.BuildOutputs);
		Assert.Equal([$"artifact_id={buildArtifactId}"], result.ProvenanceOutputs);
		Assert.Contains($"workflow run {RunId}, attempt {buildAttempt}", result.Notes);
		Assert.Contains($"/actions/runs/{RunId}/attempts/{buildAttempt}", result.Notes);
		Assert.Equal(RunId, result.Receipt.GetProperty("buildRunId").GetString());
		Assert.Equal(buildAttempt.ToString(), result.Receipt.GetProperty("buildRunAttempt").GetString());
		Assert.Equal(buildArtifactId, result.Receipt.GetProperty("buildArtifactId").GetString());
		Assert.Equal($"sha256:{ArtifactDigest}", result.Receipt.GetProperty("buildArtifactDigest").GetString());
		Assert.Equal(SourceSha, result.Receipt.GetProperty("sourceSha").GetString());
		Assert.Equal(ReleaseId, result.Receipt.GetProperty("releaseId").GetInt64());
		Assert.Equal(6, result.Receipt.GetProperty("assets").GetArrayLength());
	}

	[Theory]
	[InlineData("view", "exit-code")]
	[InlineData("view", "malformed-json")]
	[InlineData("freeze", "exit-code")]
	[InlineData("freeze", "malformed-json")]
	public async Task Workflow_RejectsReleaseApiFailureWithoutFreezeReceipt(
		string stage,
		string failure)
	{
		FixtureResult result = await RunFixtureAsync(CreateReleaseRequest(
			stage, new(Failure: failure)));

		AssertRejectedBeforeFreezeReceipt(result, stage);
		string expectedError = (stage, failure) switch
		{
			("view", "exit-code") => "Unable to inspect draft prerelease",
			("freeze", "exit-code") => "Unable to read back draft Release",
			(_, "malformed-json") => "JSON",
			_ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown release API fixture failure.")
		};
		Assert.Contains(expectedError, result.Error);
	}

	[Theory]
	[InlineData(null)]
	[InlineData(0L)]
	[InlineData("03456")]
	[InlineData("3456\n")]
	[InlineData("9223372036854775808")]
	[InlineData(1.5)]
	public async Task Workflow_RejectsInvalidReleaseIdBeforeFreezeRead(object? releaseId)
	{
		FixtureResult result = await RunFixtureAsync(CreateReleaseRequest(
			"view", new(Overrides: new() { ["databaseId"] = releaseId })));

		AssertRejectedBeforeFreezeReceipt(result, "view");
		Assert.Contains("valid numeric Release ID", result.Error);
	}

	[Theory]
	[InlineData("view", "isDraft", false)]
	[InlineData("view", "isPrerelease", false)]
	[InlineData("view", "tagName", "v0.2.1")]
	[InlineData("view", "targetCommitish", "cccccccccccccccccccccccccccccccccccccccc")]
	[InlineData("freeze", "id", 3457L)]
	[InlineData("freeze", "draft", false)]
	[InlineData("freeze", "prerelease", false)]
	[InlineData("freeze", "tag_name", "v0.2.1")]
	[InlineData("freeze", "target_commitish", "cccccccccccccccccccccccccccccccccccccccc")]
	public async Task Workflow_RejectsReleaseMetadataMismatchWithoutFreezeReceipt(
		string stage,
		string field,
		object value)
	{
		FixtureResult result = await RunFixtureAsync(CreateReleaseRequest(
			stage, new(Overrides: new() { [field] = value })));

		AssertRejectedBeforeFreezeReceipt(result, stage);
	}

	[Theory]
	[InlineData("view", "name", "unexpected.zip")]
	[InlineData("view", "size", -1L)]
	[InlineData("view", "digest", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
	[InlineData("view", null, null)]
	[InlineData("freeze", "name", "unexpected.zip")]
	[InlineData("freeze", "size", -1L)]
	[InlineData("freeze", "digest", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
	[InlineData("freeze", null, null)]
	public async Task Workflow_RejectsReleaseAssetMismatchOrMissingAssetWithoutFreezeReceipt(
		string stage,
		string? field,
		object? value)
	{
		FixtureResult result = await RunFixtureAsync(CreateReleaseRequest(
			stage, new(
				AssetOverrides: field is null ? null : new() { [field] = value },
				OmitAsset: field is null)));

		AssertRejectedBeforeFreezeReceipt(result, stage);
	}

	[Theory]
	[InlineData("BUILD_RUN_ID", "")]
	[InlineData("BUILD_RUN_ID", "12346")]
	[InlineData("BUILD_RUN_ID", "-1")]
	[InlineData("BUILD_RUN_ATTEMPT", "")]
	[InlineData("BUILD_RUN_ATTEMPT", "0")]
	[InlineData("BUILD_RUN_ATTEMPT", "01")]
	[InlineData("BUILD_RUN_ATTEMPT", "3")]
	[InlineData("BUILD_RUN_ATTEMPT", "1\n")]
	[InlineData("CANDIDATE_ARTIFACT_ID", "")]
	[InlineData("CANDIDATE_ARTIFACT_ID", "98765suffix")]
	[InlineData("CANDIDATE_ARTIFACT_ID", "9223372036854775808")]
	[InlineData("CANDIDATE_ARTIFACT_DIGEST", "")]
	[InlineData("CANDIDATE_ARTIFACT_DIGEST", "not-a-sha256")]
	public async Task Workflow_RejectsMissingOrInvalidBuildOutputsBeforeRemoteAccess(
		string field,
		string value)
	{
		FixtureResult result = await RunFixtureAsync(new(
			1,
			2,
			new() { [field] = value },
			new()));

		AssertRejectedBeforePublication(result);
		Assert.Contains("provenance", result.Error);
		Assert.Equal(0, result.ArtifactReadCount);
	}

	[Theory]
	[InlineData("id", "98766")]
	[InlineData("name", "AiUsageDashboard-0.2.0-win-x64-12345-2")]
	[InlineData("digest", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
	[InlineData("expired", true)]
	[InlineData("expired", null)]
	[InlineData("run", "12346")]
	[InlineData("sha", "cccccccccccccccccccccccccccccccccccccccc")]
	public async Task Workflow_RejectsArtifactIdentityMismatchBeforeRemoteMutation(
		string field,
		object? value)
	{
		Dictionary<string, object?> artifactOverrides = field switch
		{
			"run" => new()
			{
				["workflow_run"] = new { id = value, head_sha = SourceSha }
			},
			"sha" => new()
			{
				["workflow_run"] = new { id = RunId, head_sha = value }
			},
			_ => new() { [field] = value }
		};
		FixtureResult result = await RunFixtureAsync(new(
			1,
			2,
			new(),
			artifactOverrides));

		AssertRejectedBeforePublication(result);
		Assert.Contains($"Candidate artifact {ArtifactId} does not match build {RunId}, attempt 1", result.Error);
		Assert.Equal(1, result.ArtifactReadCount);
	}

	[Theory]
	[InlineData("exit-code", "Unable to inspect candidate artifact")]
	[InlineData("malformed-json", "JSON")]
	public async Task Workflow_RejectsArtifactApiFailureBeforeRemoteMutation(
		string failure,
		string expectedError)
	{
		FixtureResult result = await RunFixtureAsync(new(
			1,
			2,
			new(),
			new(),
			ArtifactReadFailure: failure));

		AssertRejectedBeforePublication(result);
		Assert.Contains(expectedError, result.Error);
		Assert.Equal(1, result.ArtifactReadCount);
	}

	private static void AssertRejectedBeforePublication(FixtureResult result)
	{
		Assert.Equal("provenance", result.Phase);
		Assert.NotNull(result.Error);
		Assert.Equal(0, result.RemoteMutationCount);
		Assert.Empty(result.ProvenanceOutputs);
		Assert.Null(result.Notes);
		Assert.Equal(JsonValueKind.Null, result.Receipt.ValueKind);
	}

	private static FixtureRequest CreateReleaseRequest(string stage, ReleaseApiResponse response)
	{
		return stage switch
		{
			"view" => new(1, 2, new(), new(), ReleaseView: response),
			"freeze" => new(1, 2, new(), new(), FreezeRead: response),
			_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown release API fixture stage.")
		};
	}

	private static void AssertRejectedBeforeFreezeReceipt(FixtureResult result, string stage)
	{
		Assert.Equal("publish", result.Phase);
		Assert.NotNull(result.Error);
		Assert.Equal(1, result.RemoteMutationCount);
		Assert.Equal(1, result.ReleaseViewReadCount);
		string[] expectedApiPaths = stage switch
		{
			"view" => [],
			"freeze" => [$"repos/fixture/release-candidate/releases/{ReleaseId}"],
			_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown release API fixture stage.")
		};
		Assert.Equal(expectedApiPaths, result.ReleaseApiPaths);
		Assert.Equal(JsonValueKind.Null, result.Receipt.ValueKind);
	}

	private static async Task<FixtureResult> RunFixtureAsync(FixtureRequest request)
	{
		string temporaryDirectory = Path.Combine(
			Path.GetTempPath(),
			$"AiUsageDashboard.ProvenanceTests.{Guid.NewGuid():N}");
		Directory.CreateDirectory(temporaryDirectory);
		try
		{
			string requestPath = Path.Combine(temporaryDirectory, "request.json");
			await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
			ProcessStartInfo startInfo = new("pwsh")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = temporaryDirectory
			};
			foreach (string argument in new[]
			{
				"-NoLogo", "-NoProfile", "-NonInteractive", "-File",
				Path.Combine(RepositoryTestPaths.Root, "tests", "AiUsageDashboard.Tests", "ReleaseCandidateProvenanceFixture.ps1"),
				"-WorkflowPath",
				Path.Combine(RepositoryTestPaths.Root, ".github", "workflows", "internal-release-candidate.yml"),
				"-RequestPath", requestPath
			})
			{
				startInfo.ArgumentList.Add(argument);
			}

			using Process process = new() { StartInfo = startInfo };
			Assert.True(process.Start());
			Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorTask = process.StandardError.ReadToEndAsync();
			try
			{
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
			}
			finally
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
					await process.WaitForExitAsync();
				}

				await Task.WhenAll(outputTask, errorTask);
			}

			Assert.True(process.ExitCode == 0,
				$"Workflow fixture failed ({process.ExitCode}). stdout: {outputTask.Result}; stderr: {errorTask.Result}");
			string resultPath = Path.Combine(temporaryDirectory, "result.json");
			FixtureResult? result = JsonSerializer.Deserialize<FixtureResult>(
				await File.ReadAllTextAsync(resultPath));
			Assert.NotNull(result);
			return result;
		}
		finally
		{
			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}
}
