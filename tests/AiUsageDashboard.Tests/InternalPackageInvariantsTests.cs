using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using AiUsageDashboard.Antigravity.Setup;
using AiUsageDashboard.AntigravitySpike;
using AiUsageDashboard.App.Providers;

namespace AiUsageDashboard.Tests;

public sealed class InternalPackageInvariantsTests
{
	[Fact]
	public void GlobalJson_PinsReviewedSdkWithoutRollForward()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(
			Path.Combine(repositoryRoot, "global.json")));
		JsonElement sdk = document.RootElement.GetProperty("sdk");

		Assert.Equal("8.0.425", sdk.GetProperty("version").GetString());
		Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
		Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
	}

	[Fact]
	public void AntigravityProjects_KeepProductionSourcesOwnedByProductionTree()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		XDocument productionProject = XDocument.Load(Path.Combine(
			repositoryRoot,
			"src",
			"AiUsageDashboard.Antigravity",
			"AiUsageDashboard.Antigravity.csproj"));
		Assert.DoesNotContain(
			productionProject.Descendants("Compile"),
			static item => item.Attribute("Include")?.Value.Contains(
				@"tools\AiUsageDashboard.AntigravitySpike",
				StringComparison.OrdinalIgnoreCase) == true);

		XDocument spikeProject = XDocument.Load(Path.Combine(
			repositoryRoot,
			"tools",
			"AiUsageDashboard.AntigravitySpike",
			"AiUsageDashboard.AntigravitySpike.csproj"));
		string[] productionSourceLinks = spikeProject
			.Descendants("Compile")
			.Select(static item => item.Attribute("Include")?.Value)
			.Where(static include => include?.StartsWith(
				@"..\..\src\AiUsageDashboard.Antigravity\",
				StringComparison.OrdinalIgnoreCase) == true)
			.Select(static include => Path.GetFileName(include!))
			.OrderBy(static fileName => fileName, StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(
			new[]
			{
				"AntigravityUsageR1SectionCalibration.cs",
				"AssemblyInfo.cs"
			},
			productionSourceLinks);

		XDocument captureProject = XDocument.Load(Path.Combine(
			repositoryRoot,
			"src",
			"AiUsageDashboard.AntigravityCapture",
			"AiUsageDashboard.AntigravityCapture.csproj"));
		XElement privateKeyAclSource = captureProject
			.Descendants("Compile")
			.Single(static item => string.Equals(
				item.Attribute("Link")?.Value,
				"AntigravityPrivateKeyAcl.cs",
				StringComparison.Ordinal));
		Assert.Equal(
			@"..\AiUsageDashboard.Antigravity\AntigravityPrivateKeyAcl.cs",
			privateKeyAclSource.Attribute("Include")?.Value);
	}

	[Fact]
	public void PublishScript_EnforcesPinnedSdkAndRuntimeMetadata()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string script = ReadPublishScript();

		Assert.Contains("$requiredSdkVersion = '8.0.425'", script);
		Assert.Contains("$selfContainedRuntimeVersion = '8.0.31'", script);
		Assert.Contains("Push-Location -LiteralPath $repositoryRoot", script);
		Assert.Contains("& dotnet --version", script);
		Assert.DoesNotContain("$LASTEXITCODE = 0", script);
		Assert.Contains("$publishSucceeded = $?", script);
		Assert.Contains(
			"$publishExitCode = $global:LASTEXITCODE",
			script);
		Assert.Contains(
			"if (!$publishSucceeded -or ($publishExitCode -ne 0))",
			script);
		Assert.Contains(
			"$dotnetVersionSucceeded = ($dotnetVersionExitCode -eq 0)",
			script);
		Assert.Contains(
			"$gitRevisionSucceeded = ($gitRevisionExitCode -eq 0)",
			script);
		Assert.Contains(
			"$gitStatusSucceeded = ($gitStatusExitCode -eq 0)",
			script);
		Assert.Contains("Tee-Object -Variable selectedSdkVersionOutput", script);
		Assert.Contains("Tee-Object -Variable sourceRevisionOutput", script);
		Assert.Contains("Tee-Object -Variable workingTreeStatus", script);
		Assert.Contains("git rev-parse HEAD", script);
		Assert.Contains(
			"git status --porcelain=v1 --untracked-files=all",
			script);
		Assert.Contains(
			"Formal packages require a clean Git working tree.",
			script);
		Assert.Contains(
			"$resolvedOutputRoot.StartsWith(",
			script);
		Assert.Contains(
			"$defaultOutputRootPrefix,",
			script);
		Assert.Contains("-p:Version=$Version", script);
		Assert.Contains("-p:SourceRevisionId=$sourceRevisionId", script);
		Assert.Contains("$sourceRevisionId-dirty", script);
		Assert.Contains(
			"function Assert-PublishedProductVersion",
			script);
		Assert.Contains(
			"$expectedProductVersion = \"$Version+$sourceRevisionId\"",
			script);
		Assert.Contains(
			"[System.Diagnostics.FileVersionInfo]::GetVersionInfo(",
			script);
		Assert.Equal(
			3,
			Regex.Matches(
				script,
				@"(?m)^\tAssert-PublishedProductVersion\s+`\r?$")
				.Count);
		Assert.Contains(
			"-ExecutablePath (Join-Path $appRoot 'AiUsageDashboard.App.exe')",
			script);
		Assert.Contains(
			"-ExecutablePath (Join-Path $setupPublishRoot 'AiUsageDashboard.Antigravity.Setup.exe')",
			script);
		Assert.Contains(
			"$antigravityCapturePublishRoot",
			script);
		Assert.Contains(
			"-p:PublishSingleFile=true",
			script);
		Assert.Contains(
			"-p:IncludeNativeLibrariesForSelfExtract=true",
			script);
		Assert.Contains(
			"Invoke-PinnedTrimmedSingleFilePublish",
			script);
		Assert.Single(Regex.Matches(
			script,
			@"-p:PublishTrimmed=true").Cast<Match>());
		Assert.Single(Regex.Matches(
			script,
			@"-p:TrimMode=full").Cast<Match>());
		Assert.Single(Regex.Matches(
			script,
			@"-p:SuppressTrimAnalysisWarnings=false").Cast<Match>());
		Assert.Contains(
			"-ExecutableName 'AiUsageDashboard.AntigravityCapture.exe'",
			script);
		Assert.Contains(
			"function Invoke-PublishedCaptureSmokeTest",
			script);
		Assert.Contains(
			"--ai-usage-dashboard-agy-statusline-smoke-test-v1",
			script);
		Assert.Single(Regex.Matches(
			script,
			@"(?m)^\tInvoke-PublishedCaptureSmokeTest\s+`\r?$").Cast<Match>());
		Assert.Contains("$process.WaitForExit(10000)", script);
		string captureProject = File.ReadAllText(Path.Combine(
			repositoryRoot,
			"src",
			"AiUsageDashboard.AntigravityCapture",
			"AiUsageDashboard.AntigravityCapture.csproj"));
		Assert.Equal(
			@"..\AiUsageDashboard.Licensing\AiUsageDashboard.Licensing.csproj",
			Assert.Single(XDocument.Parse(captureProject).Descendants("ProjectReference"))
				.Attribute("Include")?.Value);
		Assert.Contains("AntigravityStatusLineCapture.cs", captureProject);
		Assert.Contains("AntigravityPrivateKeyAcl.cs", captureProject);
		Assert.Contains(
			"$publishedFiles.Count -ne 1",
			script);
		Assert.Contains(
			"-p:RuntimeFrameworkVersion=$selfContainedRuntimeVersion",
			script);
		Assert.Contains("-p:TargetLatestRuntimePatch=false", script);
		foreach (string isolatedBuildFlag in new[]
		{
			"--disable-build-servers",
			"-m:1",
			"-nodeReuse:false",
			"-p:UseSharedCompilation=false"
		})
		{
			Assert.Collection(
				Regex.Matches(
					script,
					Regex.Escape(isolatedBuildFlag)).Cast<Match>(),
				_ => { },
				_ => { });
		}
		Assert.Contains("Assert-PublishedRuntime", script);
		Assert.Contains("includedFrameworks", script);
		Assert.Contains(
			"runtimepack.Microsoft.NETCore.App.Runtime.$runtimeIdentifier/",
			script);
		Assert.Contains(
			"runtimepack.Microsoft.WindowsDesktop.App.Runtime.$runtimeIdentifier/",
			script);
	}

	[Fact]
	public void SharedRuntimeProjects_PinOverriddenFrameworkPackagesToSameVersions()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string[] projectPaths =
		[
			Path.Combine(
				repositoryRoot,
				"src",
				"AiUsageDashboard.App",
				"AiUsageDashboard.App.csproj"),
			Path.Combine(
				repositoryRoot,
				"src",
				"AiUsageDashboard.Antigravity.Setup",
				"AiUsageDashboard.Antigravity.Setup.csproj")
		];
		string[] packageNames =
		[
			"System.Diagnostics.DiagnosticSource",
			"System.Text.Json"
		];

		foreach (string packageName in packageNames)
		{
			string expectedVersion = packageName == "System.Text.Json" ? "10.0.11" : "10.0.2";
			string[] sharingProjectPaths = packageName == "System.Text.Json"
				? [.. projectPaths, Path.Combine(repositoryRoot, "src", "AiUsageDashboard.ClaudeCapture", "AiUsageDashboard.ClaudeCapture.csproj")]
				: projectPaths;
			string[] versions = sharingProjectPaths
				.Select(XDocument.Load)
				.Select(document => document
					.Descendants("PackageReference")
					.Single(item => string.Equals(
						item.Attribute("Include")?.Value,
						packageName,
						StringComparison.Ordinal))
					.Attribute("Version")?.Value)
				.Where(static version => version is not null)
				.Select(static version => version!)
				.ToArray();

			Assert.Equal(sharingProjectPaths.Length, versions.Length);
			Assert.All(versions, version => Assert.Equal(expectedVersion, version));
		}
	}

	[Fact]
	public void AppProject_PublishesUserGuideAsRuntimeReadme()
	{
		string projectPath = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"AiUsageDashboard.App.csproj");
		XDocument project = XDocument.Load(projectPath);
		XElement[] contentItems = project
			.Descendants("Content")
			.ToArray();
		XElement? runtimeReadme = contentItems.SingleOrDefault(
			static item =>
				string.Equals(
					item.Attribute("Link")?.Value,
					"README.md",
					StringComparison.Ordinal));

		Assert.NotNull(runtimeReadme);
		Assert.Equal(
			@"..\..\使用說明.md",
			runtimeReadme.Attribute("Include")?.Value);
		Assert.Equal(
			"PreserveNewest",
			runtimeReadme.Attribute("CopyToOutputDirectory")?.Value);
		Assert.DoesNotContain(
			contentItems,
			static item =>
				string.Equals(
					item.Attribute("Include")?.Value,
					@"..\..\README.md",
					StringComparison.Ordinal) &&
				string.Equals(
					item.Attribute("Link")?.Value,
					"README.md",
					StringComparison.Ordinal));
	}

	[Fact]
	public void AppProject_StagesStandaloneAntigravityCaptureHelperForLocalRuns()
	{
		string projectPath = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"AiUsageDashboard.App.csproj");
		XDocument project = XDocument.Load(projectPath);
		XElement captureReference = project
			.Descendants("ProjectReference")
			.Single(item => string.Equals(
				item.Attribute("Include")?.Value,
				@"..\AiUsageDashboard.AntigravityCapture\AiUsageDashboard.AntigravityCapture.csproj",
				StringComparison.Ordinal));
		Assert.Equal(
			"false",
			captureReference.Attribute("ReferenceOutputAssembly")?.Value);
		Assert.Equal(
			"false",
			captureReference.Attribute("Private")?.Value);

		XElement stageTarget = project
			.Descendants("Target")
			.Single(item => string.Equals(
				item.Attribute("Name")?.Value,
				"StageAntigravityCaptureHelper",
				StringComparison.Ordinal));
		Assert.Equal("Build", stageTarget.Attribute("AfterTargets")?.Value);
		XElement stageProperties = Assert.Single(
			stageTarget.Elements("PropertyGroup"));
		Assert.Equal(
			"$([MSBuild]::NormalizeDirectory('$(MSBuildProjectDirectory)', '$(IntermediateOutputPath)', 'antigravity-capture-publish'))",
			stageProperties.Element("AntigravityCapturePublishDirectory")?.Value);

		XElement publish = Assert.Single(stageTarget.Elements("MSBuild"));
		Assert.Equal("Publish", publish.Attribute("Targets")?.Value);
		string properties = publish.Attribute("Properties")?.Value ?? string.Empty;
		Assert.Contains("RuntimeIdentifier=win-x64", properties);
		Assert.Contains("SelfContained=true", properties);
		Assert.Contains("PublishSingleFile=true", properties);
		Assert.Contains("PublishTrimmed=true", properties);

		XElement copy = Assert.Single(stageTarget.Elements("Copy"));
		Assert.Equal(
			"$(AntigravityCapturePublishedExecutable)",
			copy.Attribute("SourceFiles")?.Value);
		Assert.Equal(
			"$(AntigravityCaptureOutputExecutable)",
			copy.Attribute("DestinationFiles")?.Value);
	}

	[Fact]
	public void PublishScript_PackagesOnlyUserFacingGuides()
	{
		string script = ReadPublishScript();

		Assert.Contains(
			"Join-Path $repositoryRoot '使用說明.md'",
			script);
		Assert.Contains(
			"Join-Path $stagedPackageRoot '使用說明.md'",
			script);
		Assert.Contains(
			"(Join-Path $appRoot 'README.md')",
			script);
		Assert.DoesNotContain(
			"Join-Path $repositoryRoot 'third-party-notices'",
			script);
		Assert.Contains("'](app/third-party-notices/'", script);
		Assert.Contains("'AiUsageDashboard.Updater.Core.UpdatePackageStager'", script);
		Assert.Contains("$stager.StageAsync(", script);
		Assert.Single(
			Regex.Matches(
				script,
				Regex.Escape(
					"'third-party-notices\\GitHub-Copilot-CLI-LICENSE.md'")));
		Assert.Single(
			Regex.Matches(
				script,
				Regex.Escape(
					"'third-party-notices\\GitHub-Copilot-SDK-LICENSE.md'")));
		Assert.DoesNotContain(
			"Join-Path $repositoryRoot 'INTERNAL_DISTRIBUTION.md'",
			script);
		Assert.DoesNotContain(
			"(Join-Path $stagedPackageRoot 'INTERNAL_DISTRIBUTION.md')",
			script);
		Assert.Contains("'INTERNAL_DISTRIBUTION.md',", script);
	}

	[Fact]
	public void UserGuide_RelativeLinksResolveInRepositoryAndRuntimeReadme()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string userGuide = File.ReadAllText(Path.Combine(
			repositoryRoot,
			"使用說明.md"));
		IEnumerable<string> inlineLinkTargets = Regex.Matches(
			userGuide,
			@"\]\((?<target>[^)]+)\)")
			.Cast<Match>()
			.Select(static match => match.Groups["target"].Value);
		IEnumerable<string> referenceLinkTargets = Regex.Matches(
			userGuide,
			@"(?m)^\[[^\]]+\]:\s+(?<target>\S+)")
			.Cast<Match>()
			.Select(static match => match.Groups["target"].Value);
		string[] relativeLinks = inlineLinkTargets
			.Concat(referenceLinkTargets)
			.Where(static target =>
				!target.StartsWith('#') &&
				!Uri.TryCreate(target, UriKind.Absolute, out _))
			.Select(static target => target.Split('#', 2)[0])
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		Assert.NotEmpty(relativeLinks);

		XDocument appProject = XDocument.Load(Path.Combine(
			repositoryRoot,
			"src",
			"AiUsageDashboard.App",
			"AiUsageDashboard.App.csproj"));
		string[] runtimeFiles = appProject
			.Descendants("Content")
			.Select(static item => item.Attribute("Link")?.Value)
			.Where(static value => value is not null)
			.Select(static value => value!.Replace('\\', '/'))
			.ToArray();

		foreach (string relativeLink in relativeLinks)
		{
			Assert.StartsWith(
				"third-party-notices/",
				relativeLink,
				StringComparison.OrdinalIgnoreCase);
			Assert.True(
				File.Exists(Path.Combine(
					repositoryRoot,
					relativeLink.Replace('/', Path.DirectorySeparatorChar))),
				$"User guide link target does not exist: {relativeLink}");
			Assert.Contains(
				runtimeFiles,
				path => string.Equals(
					path,
					relativeLink,
					StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void PublishScript_KeepsVersionedOutputsButUsesShortInternalPaths()
	{
		string script = ReadPublishScript();

		Assert.Contains(
			"$archiveRootName = 'AiUsageDashboard'",
			script);
		Assert.Contains(
			"$packageName = \"AiUsageDashboard-$Version-win-x64\"",
			script);
		Assert.Contains(
			"$packageRoot = Join-Path $resolvedOutputRoot $packageName",
			script);
		Assert.Contains(
			"$zipPath = Join-Path $resolvedOutputRoot \"$packageName.zip\"",
			script);
		Assert.Contains(
			"-ChildPath \".aud-build-$([Guid]::NewGuid().ToString('N'))\"",
			script);
		Assert.Contains(
			"$stagedPackageRoot = Join-Path $workingRoot $archiveRootName",
			script);
		Assert.DoesNotContain(
			"$stagedPackageRoot = Join-Path $workingRoot $packageName",
			script);
	}

	[Fact]
	public void UserAndDistributionGuides_ListAllReviewedAntigravityVersions()
	{
		string userGuide = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"使用說明.md"));
		string distributionGuide = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"INTERNAL_DISTRIBUTION.md"));

		AssertAntigravityVersionPolicyIsDocumented(userGuide);
		AssertAntigravityVersionPolicyIsDocumented(distributionGuide);
	}

	[Fact]
	public void UserGuide_ExplainsChecksumIntegrityAndTrustedSource()
	{
		string userGuide = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"使用說明.md"));
		string distributionGuide = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"INTERNAL_DISTRIBUTION.md"));

		Assert.Contains(
			"從另一個可信任管道取得同版本的 SHA-256",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"不要只使用和 ZIP 一起轉傳的雜湊值",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"不能單獨證明檔案由誰發布",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"可略過手動 SHA-256 比對",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"驗證三份相鄰的 `.sha256` 後",
			distributionGuide,
			StringComparison.Ordinal);
	}

	[Fact]
	public void Guides_DescribeCurrentRecoveryAndSortingBehavior()
	{
		string userGuide = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"使用說明.md"));
		string technicalOverview = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"docs",
			"TECHNICAL_OVERVIEW.md"));

		Assert.Contains(
			"暫時失敗不代表需要重新登入",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"若程式無法確認上次檢查是否完成",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"1、2、之後每 4 分鐘",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"背景會約在 1、2 分鐘後重試，之後每 4 分鐘持續重試",
			technicalOverview,
			StringComparison.Ordinal);
		Assert.Contains(
			"只有無法自動安全恢復、確實需要本人確認時",
			technicalOverview,
			StringComparison.Ordinal);
		Assert.Contains(
			"**重新檢查 Claude 用量**",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"**重新驗證 Claude 用量**",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"**重新啟動 AI Usage**",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"程式會停止後續更新",
			userGuide,
			StringComparison.Ordinal);
		int automaticRepairIndex = userGuide.IndexOf(
			"**Antigravity 更新後仍無法讀取**",
			StringComparison.Ordinal);
		Assert.True(automaticRepairIndex >= 0);
		Assert.True(userGuide.IndexOf(
			"既有連接通常可沿用",
			automaticRepairIndex,
			StringComparison.Ordinal) > automaticRepairIndex);
		Assert.True(userGuide.IndexOf(
			"若卡片要求操作，請重新確認連接",
			automaticRepairIndex,
			StringComparison.Ordinal) > automaticRepairIndex);
		Assert.True(userGuide.IndexOf(
			"請更新 AI Usage",
			automaticRepairIndex,
			StringComparison.Ordinal) > automaticRepairIndex);
		Assert.Contains(
			"保留上次的帳號與用量",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"重新啟動程式後也會繼續",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"**Antigravity 顯示用量檢查已暫停**",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"**重新檢查 Antigravity 用量**",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"依服務、主要用量週期與重置時間排列",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"不看用量百分比",
			userGuide,
			StringComparison.Ordinal);
	}

	[Fact]
	public void PublishScript_FailsClosedWhenSharedRuntimeFilesDiffer()
	{
		string script = ReadPublishScript();

		Assert.Contains("Merge-PublishDirectory", script);
		Assert.Contains("Get-FileHash", script);
		Assert.Contains(
			"Shared-runtime file collision differs by SHA-256",
			script);
		Assert.Contains(
			"-SourceRoot $setupPublishRoot",
			script);
		Assert.Contains(
			"-DestinationRoot $appRoot",
			script);
		Assert.Contains(
			"-SourceRoot $antigravityCapturePublishRoot",
			script);
		Assert.Contains(
			"(Join-Path $appRoot 'AiUsageDashboard.AntigravityCapture.exe')",
			script);
		Assert.DoesNotContain(
			"(Join-Path $stagedPackageRoot 'setup')",
			script);
	}

	[Fact]
	public void PublishScript_FinalizesOnlyCompletedTemporaryOutputs()
	{
		string script = ReadPublishScript();

		Assert.Contains("[Guid]::NewGuid()", script);
		Assert.Contains(
			"-LiteralPath $stagedPackageRoot",
			script);
		Assert.Contains(
			"-DestinationPath $temporaryZipPath",
			script);
		Assert.Contains(
			"[System.IO.Directory]::Move($stagedPackageRoot, $packageRoot)",
			script);
		Assert.Contains(
			"[System.IO.File]::Move($temporaryZipPath, $zipPath)",
			script);
		Assert.Contains(
			"$temporaryChecksumPath,",
			script);
		Assert.Contains("$checksumPath)", script);
		Assert.DoesNotContain(
			"Move-Item -LiteralPath $stagedPackageRoot",
			script);
		Assert.Contains("$packageRootFinalized = $true", script);
		Assert.Contains("$zipFinalized = $true", script);
		Assert.Contains("$checksumFinalized = $true", script);
	}

	[Fact]
	public void PublishScript_RemovesWorkingRootBeforeReportingSuccess()
	{
		string script = ReadPublishScript();
		const string Cleanup = "\tRemove-OwnedPath -Path $workingRoot -Recurse";
		const string SuccessOutput = "\tWrite-Output \"SDK: $selectedSdkVersion\"";

		int cleanupIndex = script.IndexOf(Cleanup, StringComparison.Ordinal);
		int successOutputIndex = script.IndexOf(
			SuccessOutput,
			StringComparison.Ordinal);

		Assert.True(cleanupIndex >= 0);
		Assert.True(successOutputIndex > cleanupIndex);
		Assert.Contains("@($workingRoot, $true, $true)", script);
		Assert.Contains("Cleanup did not remove owned path", script);
		Assert.Contains("$resolvedOutputRootPrefix", script);
		Assert.Contains("Refusing to clean a path outside the owned output tree", script);
		Assert.Contains("[IO.FileAttributes]::ReparsePoint", script);
		Assert.Contains("Refusing to clean a tree containing reparse points", script);
		Assert.DoesNotContain("-ErrorAction SilentlyContinue", script);
	}

	[Fact]
	public async Task DashboardSetupLauncher_WhenSharedHelperExists_UsesAppDirectory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		Directory.CreateDirectory(appDirectory);
		string setupExecutablePath = Path.Combine(
			appDirectory,
			"AiUsageDashboard.Antigravity.Setup.exe");
		File.WriteAllBytes(setupExecutablePath, new byte[] { 0 });
		ProcessStartInfo? observed = null;
		AntigravityAccountSetupLauncher launcher = new(
			appDirectory,
			(startInfo, _) =>
			{
				observed = startInfo;
				return Task.FromResult<int?>(
					AntigravityMachineSetupLaunchArguments
						.DashboardManagedSuccessExitCode);
			});

		AntigravityAccountSetupOutcome outcome =
			await launcher.RunAsync(CancellationToken.None);

		Assert.Equal(
			AntigravityAccountSetupOutcome.Completed,
			outcome);
		Assert.NotNull(observed);
		Assert.Equal(setupExecutablePath, observed.FileName);
		Assert.Equal(appDirectory, observed.WorkingDirectory);
	}

	[Fact]
	public void SetupDashboardLauncher_WhenSharedAppExists_UsesAppDirectory()
	{
		using TemporaryDirectory temporaryDirectory = new();
		string appDirectory = Path.Combine(
			temporaryDirectory.Path,
			"app");
		Directory.CreateDirectory(appDirectory);
		string appExecutablePath = Path.Combine(
			appDirectory,
			"AiUsageDashboard.App.exe");
		File.WriteAllBytes(appExecutablePath, new byte[] { 0 });
		ProcessStartInfo? observed = null;

		bool opened = DashboardLauncher.TryOpen(
			appDirectory,
			startInfo =>
			{
				observed = startInfo;
				return Process.GetCurrentProcess();
			});

		Assert.True(opened);
		Assert.NotNull(observed);
		Assert.Equal(appExecutablePath, observed.FileName);
		Assert.Equal(appDirectory, observed.WorkingDirectory);
		Assert.Contains(
			AntigravityMachineSetupLaunchArguments.EnsureDashboardAccount,
			observed.ArgumentList);
	}

	[Fact]
	public void DistributionDocs_UseSharedRuntimeHelperPath()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string technicalOverview = File.ReadAllText(Path.Combine(
			repositoryRoot,
			"docs",
			"TECHNICAL_OVERVIEW.md"));
		string distributionGuide = File.ReadAllText(
			Path.Combine(repositoryRoot, "INTERNAL_DISTRIBUTION.md"));

		Assert.Contains(
			"app\\AiUsageDashboard.Antigravity.Setup.exe",
			technicalOverview);
		Assert.Contains(
			"app\\AiUsageDashboard.Antigravity.Setup.exe",
			distributionGuide);
		Assert.DoesNotContain(
			"setup\\AiUsageDashboard.Antigravity.Setup.exe",
			technicalOverview);
		Assert.DoesNotContain(
			"setup\\AiUsageDashboard.Antigravity.Setup.exe",
			distributionGuide);
	}

	[Fact]
	public void ProviderDocumentation_ListsGrokAndCopilotAcrossMaintainerAndReleaseSurfaces()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string readme = File.ReadAllText(
			Path.Combine(repositoryRoot, "README.md"));
		string technicalOverview = File.ReadAllText(Path.Combine(
			repositoryRoot,
			"docs",
			"TECHNICAL_OVERVIEW.md"));
		string implementationChecklist = File.ReadAllText(
			Path.Combine(repositoryRoot, "IMPLEMENTATION_CHECKLIST.md"));
		string releasingGuide = File.ReadAllText(
			Path.Combine(repositoryRoot, "RELEASING.md"));
		string distributionGuide = File.ReadAllText(
			Path.Combine(repositoryRoot, "INTERNAL_DISTRIBUTION.md"));
		string userGuide = File.ReadAllText(
			Path.Combine(repositoryRoot, "使用說明.md"));

		Assert.Contains(
			"Claude、Codex、GitHub Copilot 與 Grok 可加入多個帳號；" +
			"Antigravity 目前限一個帳號。",
			readme,
			StringComparison.Ordinal);
		Assert.Contains("| Grok |", technicalOverview);
		Assert.Contains("| Grok |", implementationChecklist);
		Assert.Contains("| Grok |", distributionGuide);
		Assert.Contains("- [ ] Grok：", distributionGuide);
		Assert.Contains(
			"| Grok | 從 xAI 官方來源安裝到預設位置的 Grok Build CLI。",
			userGuide,
			StringComparison.Ordinal);

		Assert.Contains("| GitHub Copilot |", technicalOverview);
		Assert.Contains("| GitHub Copilot |", implementationChecklist);
		Assert.Contains("| GitHub Copilot |", distributionGuide);
		Assert.Contains(
			"Claude、Codex、GitHub Copilot、Grok 與 AGY",
			releasingGuide,
			StringComparison.Ordinal);
		Assert.Contains("- [ ] GitHub Copilot：", distributionGuide);
		Assert.Contains(
			"| GitHub Copilot | 可使用 Copilot 的 `github.com` 帳號；",
			userGuide,
			StringComparison.Ordinal);
	}

	[Fact]
	public void CliVersionGuides_SeparateUserRequirementsFromTechnicalVersionPolicy()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string readme = File.ReadAllText(
			Path.Combine(repositoryRoot, "README.md"));
		string userGuide = File.ReadAllText(
			Path.Combine(repositoryRoot, "使用說明.md"));
		string distributionGuide = File.ReadAllText(
			Path.Combine(repositoryRoot, "INTERNAL_DISTRIBUTION.md"));
		string technicalOverview = File.ReadAllText(Path.Combine(
			repositoryRoot,
			"docs",
			"TECHNICAL_OVERVIEW.md"));

		Assert.DoesNotContain(
			"0.144.1",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"1.0.3",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"Claude Code 2.1.169 以上版本",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"1.1.11 以上、2.0.0 未滿",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"相容性基準",
			userGuide,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"--safe-mode",
			userGuide,
			StringComparison.Ordinal);

		foreach (string document in new[]
		{
			distributionGuide,
			technicalOverview
		})
		{
			Assert.Contains("相容性基準", document, StringComparison.Ordinal);
			Assert.Contains("0.144.1", document, StringComparison.Ordinal);
			Assert.Contains("1.0.3", document, StringComparison.Ordinal);
		}

		Assert.Contains(
			"SafetyBoundaryRejected",
			technicalOverview,
			StringComparison.Ordinal);
		Assert.Contains(
			"--safe-mode",
			technicalOverview,
			StringComparison.Ordinal);
		Assert.Contains(
			"版本差異可能是原因之一",
			technicalOverview,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Grok Build CLI `1.0.3` 以上版本",
			readme,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Grok Build CLI `1.0.3` 以上版本",
			userGuide,
			StringComparison.Ordinal);
	}

	[Fact]
	public void SetupWindow_DashboardManagedShutdownPaths_UseSharedProtocolAwareExitMapping()
	{
		string source = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.Antigravity.Setup",
			"SetupWindow.xaml.cs"));
		string cancelAndCloseBody = ReadMethodBody(
			source,
			"private async Task CancelAndCloseAsync()");
		string closeAfterDisposalBody = ReadMethodBody(
			source,
			"private async Task CloseAfterWorkflowDisposalAsync(");
		string shutdownBody = ReadMethodBody(
			source,
			"private void ShutdownDashboardManaged()");

		Assert.True(
			Regex.IsMatch(
				cancelAndCloseBody,
				@"if \(_isDashboardManaged\)\s*\{\s*ShutdownDashboardManaged\(\);\s*return;",
				RegexOptions.Singleline),
			"Cancelling a dashboard-managed helper must use the shared shutdown path.");
		Assert.True(
			Regex.IsMatch(
				closeAfterDisposalBody,
				@"if \(completeDashboardManagedLaunch\)\s*\{\s*ShutdownDashboardManaged\(\);\s*return;",
				RegexOptions.Singleline),
			"Normal dashboard-managed completion must use the shared shutdown path.");
		Assert.DoesNotContain(
			"Application.Current.Shutdown",
			cancelAndCloseBody,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Application.Current.Shutdown",
			closeAfterDisposalBody,
			StringComparison.Ordinal);
		Assert.Single(Regex.Matches(
			source,
			@"System\.Windows\.Application\.Current\.Shutdown\(")
			.Cast<Match>());
		Assert.Contains(
			"ResolveDashboardManagedCompletionExitCode(",
			shutdownBody,
			StringComparison.Ordinal);
		Assert.Contains(
			"_workflow.DashboardManagedExitCode",
			shutdownBody,
			StringComparison.Ordinal);
		Assert.Contains(
			"DashboardManagedResultProtocolEnvironmentVariable",
			shutdownBody,
			StringComparison.Ordinal);

		Assert.Equal(
			AntigravityMachineSetupLaunchArguments
				.DashboardManagedSuccessExitCode,
			SetupWindow.ResolveDashboardManagedCompletionExitCode(
				AntigravityMachineSetupLaunchArguments
					.DashboardManagedOfficialPrintSuccessExitCode,
				resultProtocol: null));
	}

	private static string ReadPublishScript()
	{
		return File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"tools",
			"Publish-Internal.ps1"));
	}

	private static string ReadMethodBody(
		string source,
		string signature)
	{
		int signatureIndex = source.IndexOf(
			signature,
			StringComparison.Ordinal);
		if (signatureIndex < 0)
		{
			throw new InvalidOperationException(
				$"Method signature was not found: {signature}");
		}

		int openingBraceIndex = source.IndexOf('{', signatureIndex);
		if (openingBraceIndex < 0)
		{
			throw new InvalidOperationException(
				$"Method body was not found: {signature}");
		}

		int braceDepth = 0;
		for (int index = openingBraceIndex; index < source.Length; index++)
		{
			switch (source[index])
			{
				case '{':
					braceDepth++;
					break;
				case '}':
					braceDepth--;
					if (braceDepth == 0)
					{
						return source[openingBraceIndex..(index + 1)];
					}

					break;
			}
		}

		throw new InvalidOperationException(
			$"Method body was not terminated: {signature}");
	}

	private static void AssertAntigravityVersionPolicyIsDocumented(
		string document)
	{
		Assert.Contains("1.1.7", document, StringComparison.Ordinal);
		Assert.Contains("1.1.9", document, StringComparison.Ordinal);
		Assert.True(
			document.Contains(
				"`1.1.11` 以上",
				StringComparison.Ordinal) ||
			document.Contains(
				"1.1.11 以上",
				StringComparison.Ordinal) ||
			document.Contains(
				"1.1.11 <= version",
				StringComparison.Ordinal) ||
			document.Contains(
				"version >= 1.1.11",
				StringComparison.Ordinal),
			"The AGY guide must document the official >=1.1.11 boundary.");
		Assert.True(
			document.Contains(
				"低於 `2.0.0`",
				StringComparison.Ordinal) ||
			document.Contains(
				"2.0.0 未滿",
				StringComparison.Ordinal) ||
			document.Contains(
				"version < 2.0.0",
				StringComparison.Ordinal),
			"The AGY guide must document the official <2.0.0 boundary.");
	}
}
