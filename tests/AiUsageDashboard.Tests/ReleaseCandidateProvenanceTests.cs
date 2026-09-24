using System.Diagnostics;
using System.Text.Json;

namespace AiUsageDashboard.Tests;

public sealed class ReleaseCandidateProvenanceTests
{
	private sealed record ReleaseApiResponse(
		string? Failure = null,
		Dictionary<string, object?>? Overrides = null,
		Dictionary<string, object?>? AssetOverrides = null,
		bool OmitAsset = false,
		string? AssetName = null);

	private sealed record FixtureRequest(
		int BuildAttempt = 1,
		int PublishAttempt = 1,
		bool RepositoryPrivate = false,
		bool CreateDraftRelease = true,
		string? RemoteMainSha = null,
		string? RemoteMainShaAtFreeze = null,
		ReleaseApiResponse? ReleaseView = null,
		ReleaseApiResponse? FreezeRead = null,
		bool ReuseUpdater = false,
		Dictionary<string, object?>? CandidateUpdaterOverrides = null);

	private sealed record FixtureResult(
		string Phase,
		string? Error,
		int RemoteMutationCount,
		int ReleaseViewReadCount,
		string[] ReleaseApiPaths,
		string[] BuildOutputs,
		string[] ReleaseOutputs,
		string? Notes,
		JsonElement Receipt,
		JsonElement Feed,
		JsonElement PreviousFeed);

	private const string RunId = "12345";
	private const long ReleaseId = 3456;

	[Fact]
	public void Workflow_UploadsOnlyCheckedDiagnosticsAndIdentityReceipt()
	{
		string workflow = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root, ".github", "workflows", "internal-release-candidate.yml"));

		Assert.Contains("head_sha=$env:GITHUB_SHA&event=push&status=success", workflow);
		Assert.Contains("$releaseRoot = Join-Path $env:RUNNER_TEMP 'internal-release'", workflow);
		Assert.Contains("gh release create $tag @assets", workflow);
		Assert.Contains("$remoteAsset[0].apiUrl", workflow);
		Assert.Contains("DRAFT_ASSET_URL: ${{ steps.candidate_release.outputs.draft_asset_url }}", workflow);
		Assert.DoesNotContain("releases/download/v$env:RELEASE_VERSION/AiUsageDashboard-update-stable.json.sha256", workflow);
		Assert.Contains("--draft", workflow);
		Assert.DoesNotContain("--draft=false", workflow);
		Assert.DoesNotContain("actions/download-artifact@", workflow);
		Assert.DoesNotContain("- name: Upload versioned candidate", workflow);
		Assert.Equal(2, workflow.Split("actions/upload-artifact@", StringSplitOptions.None).Length - 1);
		Assert.Contains("path: ${{ runner.temp }}/candidate-freeze.json", workflow);
		Assert.Contains("if: always() && steps.candidate_release.outcome == 'success'", workflow);
		Assert.Contains("path: ${{ runner.temp }}/safe-test-diagnostics", workflow);
		Assert.Contains("- name: Verify draft is unavailable anonymously", workflow);
		Assert.Contains("$env:GITHUB_REPOSITORY/releases/$releaseId", workflow);
		Assert.Contains("$sidecarAsset[0].browser_download_url", workflow);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(3)]
	public async Task Workflow_FreezesSameRunAttemptAndSixAssetIdentities(int attempt)
	{
		FixtureResult result = await RunFixtureAsync(new(BuildAttempt: attempt, PublishAttempt: attempt));

		Assert.Null(result.Error);
		Assert.Equal("complete", result.Phase);
		Assert.Equal(1, result.RemoteMutationCount);
		Assert.Equal(1, result.ReleaseViewReadCount);
		Assert.Equal([$"repos/fixture/release-candidate/releases/{ReleaseId}"], result.ReleaseApiPaths);
		Assert.Contains($"build_run_id={RunId}", result.BuildOutputs);
		Assert.Contains($"build_run_attempt={attempt}", result.BuildOutputs);
		Assert.Contains($"workflow run {RunId}, attempt {attempt}", result.Notes);
		Assert.Equal(RunId, result.Receipt.GetProperty("buildRunId").GetString());
		Assert.Contains("release_id=3456", result.ReleaseOutputs);
		Assert.Contains(
			"draft_asset_url=https://github.com/fixture/release-candidate/releases/download/untagged-fixture-uuid/AiUsageDashboard-update-stable.json.sha256",
			result.ReleaseOutputs);
		Assert.Equal(attempt.ToString(), result.Receipt.GetProperty("buildRunAttempt").GetString());
		Assert.False(result.Receipt.TryGetProperty("buildArtifactId", out _));
		Assert.Equal(ReleaseId, result.Receipt.GetProperty("releaseId").GetInt64());
		Assert.Equal(6, result.Receipt.GetProperty("assets").GetArrayLength());
	}

	[Fact]
	public async Task Workflow_FreezesReusedUpdaterWithPreviousIdentityAndCandidateAssetUrl()
	{
		FixtureResult result = await RunFixtureAsync(new(ReuseUpdater: true));

		Assert.Null(result.Error);
		Assert.Equal("complete", result.Phase);
		Assert.Equal(1, result.RemoteMutationCount);
		Assert.Equal("0.2.0", result.Feed.GetProperty("package").GetProperty("version").GetString());
		Assert.Equal("0.1.0", result.Feed.GetProperty("minimumUpdaterVersion").GetString());
		JsonElement updater = result.Feed.GetProperty("updater");
		JsonElement previousUpdater = result.PreviousFeed.GetProperty("updater");
		Assert.Equal("0.1.0", updater.GetProperty("version").GetString());
		Assert.Equal(new string('b', 40), updater.GetProperty("sourceRevision").GetString());
		Assert.Equal(previousUpdater.GetProperty("sha256").GetString(), updater.GetProperty("sha256").GetString());
		Assert.Equal(
			"https://github.com/fixture/release-candidate/releases/download/v0.1.0/AiUsageDashboard-Updater-0.1.0-win-x64.exe",
			previousUpdater.GetProperty("downloadUrl").GetString());
		Assert.Equal(
			"https://github.com/fixture/release-candidate/releases/download/v0.2.0/AiUsageDashboard-Updater-0.1.0-win-x64.exe",
			updater.GetProperty("downloadUrl").GetString());
		Assert.Contains(
			"AiUsageDashboard-Updater-0.1.0-win-x64.exe",
			result.Receipt.GetProperty("assets").EnumerateArray().Select(asset => asset.GetProperty("name").GetString()));
		Assert.Equal(6, result.Receipt.GetProperty("assets").GetArrayLength());
	}

	[Theory]
	[InlineData("version", "0.2.0")]
	[InlineData("sourceRevision", "cccccccccccccccccccccccccccccccccccccccc")]
	[InlineData("downloadUrl", "https://github.com/fixture/release-candidate/releases/download/v0.1.0/AiUsageDashboard-Updater-0.1.0-win-x64.exe")]
	public async Task Workflow_RejectsReusedUpdaterWithWrongCandidateIdentity(string field, string value)
	{
		FixtureResult result = await RunFixtureAsync(new(
			ReuseUpdater: true,
			CandidateUpdaterOverrides: new() { [field] = value }));

		Assert.Equal("freeze", result.Phase);
		Assert.Contains("Candidate feed no longer matches the verified release files", result.Error);
		Assert.Equal(0, result.RemoteMutationCount);
		Assert.Equal(JsonValueKind.Null, result.Receipt.ValueKind);
	}

	[Theory]
	[InlineData(1, 2, false, "build run identity changed")]
	[InlineData(1, 1, true, "requires a public repository")]
	public async Task Workflow_RejectsInvalidContextBeforeRemoteMutation(
		int buildAttempt,
		int publishAttempt,
		bool repositoryPrivate,
		string expectedError)
	{
		FixtureResult result = await RunFixtureAsync(new(buildAttempt, publishAttempt, repositoryPrivate));

		Assert.Equal("freeze", result.Phase);
		Assert.Contains(expectedError, result.Error);
		Assert.Equal(0, result.RemoteMutationCount);
		Assert.Equal(JsonValueKind.Null, result.Receipt.ValueKind);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Workflow_RejectsMainMovingAwayFromExpectedSha(bool afterBuild)
	{
		const string changedSha = "cccccccccccccccccccccccccccccccccccccccc";
		FixtureResult result = await RunFixtureAsync(afterBuild
			? new(RemoteMainShaAtFreeze: changedSha)
			: new(RemoteMainSha: changedSha));

		Assert.Equal(afterBuild ? "freeze" : "source", result.Phase);
		Assert.Contains("Remote main", result.Error);
		Assert.Equal(0, result.RemoteMutationCount);
		Assert.Equal(JsonValueKind.Null, result.Receipt.ValueKind);
	}

	[Fact]
	public async Task Workflow_RejectsBuildWithoutDraftBeforeSigning()
	{
		FixtureResult result = await RunFixtureAsync(new(CreateDraftRelease: false));

		Assert.Equal("source", result.Phase);
		Assert.Contains("Saving the verified candidate as a draft Release must be selected", result.Error);
		Assert.Equal(0, result.RemoteMutationCount);
		Assert.Equal(JsonValueKind.Null, result.Receipt.ValueKind);
	}

	[Theory]
	[InlineData("view", "exit-code")]
	[InlineData("view", "malformed-json")]
	[InlineData("freeze", "exit-code")]
	[InlineData("freeze", "malformed-json")]
	public async Task Workflow_RejectsReleaseApiFailureWithoutFreezeReceipt(string stage, string failure)
	{
		FixtureResult result = await RunFixtureAsync(CreateRequest(stage, new(Failure: failure)));

		AssertRejectedAfterDraftCreation(result, stage);
	}

	[Theory]
	[InlineData(null)]
	[InlineData(0L)]
	[InlineData("03456")]
	[InlineData("3456\n")]
	[InlineData("9223372036854775808")]
	[InlineData(1.5)]
	public async Task Workflow_RejectsMalformedReleaseIdWithoutFreezeReceipt(object? releaseId)
	{
		FixtureResult result = await RunFixtureAsync(new(
			ReleaseView: new(Overrides: new() { ["databaseId"] = releaseId })));

		AssertRejectedAfterDraftCreation(result, "view");
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
	public async Task Workflow_RejectsReleaseIdentityChangeWithoutFreezeReceipt(
		string stage,
		string field,
		object value)
	{
		FixtureResult result = await RunFixtureAsync(CreateRequest(
			stage, new(Overrides: new() { [field] = value })));

		AssertRejectedAfterDraftCreation(result, stage);
	}

	[Theory]
	[InlineData("view", "name", "unexpected.zip")]
	[InlineData("view", "size", -1L)]
	[InlineData("view", "digest", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
	[InlineData("view", "apiUrl", "https://api.github.com/repos/fixture/release-candidate/releases/assets/0")]
	[InlineData("view", "apiUrl", "https://api.github.com/repos/other/release-candidate/releases/assets/1")]
	[InlineData("view", null, null)]
	[InlineData("freeze", "name", "unexpected.zip")]
	[InlineData("freeze", "size", -1L)]
	[InlineData("freeze", "digest", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
	[InlineData("freeze", "id", 99L)]
	[InlineData("freeze", "browser_download_url", "https://github.com/other/release-candidate/releases/download/untagged-fixture-uuid/AiUsageDashboard-update-stable.json.sha256")]
	[InlineData("freeze", "browser_download_url", "https://github.com/fixture/release-candidate/releases/download/untagged-fixture-uuid/other.sha256")]
	[InlineData("freeze", null, null)]
	public async Task Workflow_RejectsAssetIdentityChangeWithoutFreezeReceipt(
		string stage,
		string? field,
		object? value)
	{
		FixtureResult result = await RunFixtureAsync(CreateRequest(
			stage, new(
				AssetOverrides: field is null ? null : new() { [field] = value },
				OmitAsset: field is null,
				AssetName: field == "browser_download_url"
					? "AiUsageDashboard-update-stable.json.sha256"
					: null)));

		AssertRejectedAfterDraftCreation(result, stage);
	}

	[Fact]
	public async Task Workflow_RejectsDifferentRestAssetIdBetweenViewAndFreeze()
	{
		FixtureResult result = await RunFixtureAsync(new(
			ReleaseView: new(AssetOverrides: new()
			{
				["apiUrl"] = "https://api.github.com/repos/fixture/release-candidate/releases/assets/99"
			})));

		AssertRejectedAfterDraftCreation(result, "freeze");
		Assert.Contains("changed before freezing its identity", result.Error);
	}

	private static FixtureRequest CreateRequest(string stage, ReleaseApiResponse response)
	{
		return stage switch
		{
			"view" => new(ReleaseView: response),
			"freeze" => new(FreezeRead: response),
			_ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown API fixture stage.")
		};
	}

	private static void AssertRejectedAfterDraftCreation(FixtureResult result, string stage)
	{
		Assert.Equal("freeze", result.Phase);
		Assert.NotNull(result.Error);
		Assert.Equal(1, result.RemoteMutationCount);
		Assert.Equal(1, result.ReleaseViewReadCount);
		Assert.Equal(
			stage == "freeze" ? [$"repos/fixture/release-candidate/releases/{ReleaseId}"] : [],
			result.ReleaseApiPaths);
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
			FixtureResult? result = JsonSerializer.Deserialize<FixtureResult>(
				await File.ReadAllTextAsync(Path.Combine(temporaryDirectory, "result.json")));
			Assert.NotNull(result);
			return result;
		}
		finally
		{
			Directory.Delete(temporaryDirectory, recursive: true);
		}
	}
}
