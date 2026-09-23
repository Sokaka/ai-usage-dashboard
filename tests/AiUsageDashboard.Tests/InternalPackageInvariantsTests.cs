using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiUsageDashboard.Tests;

public sealed class InternalPackageInvariantsTests
{
	[Fact]
	public async Task PackageCliCheck_AllowsProductExecutablesAndLibraryDependencies()
	{
		using TemporaryDirectory temporaryDirectory = new();
		foreach (string relativePath in new[]
		{
			"AiUsageDashboard.App.exe", "AiUsageDashboard.ClaudeCapture.exe",
			"GitHub.Copilot.SDK.dll", "coreclr.dll", "D3DCompiler_47_cor3.dll",
			"README.md", "third-party-notices/GitHub-Copilot-SDK-LICENSE.md"
		})
		{
			string path = Path.Combine(temporaryDirectory.Path, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, "Synthetic package file; never executed.");
		}

		(int exitCode, string output) = await RunPackageCliCheckAsync(temporaryDirectory.Path);
		Assert.True(exitCode == 0, output);
		Assert.Contains("7 files", output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("runtimes/win-x64/native/copilot.exe")]
	[InlineData("runtimes/win-arm64/native/COPILOT.EXE")]
	[InlineData("runtimes/win-x64/native/copilot_runtime.dll")]
	[InlineData("runtimes/linux-x64/native/libcopilot_runtime.so")]
	[InlineData("runtimes/osx-arm64/native/libcopilot_runtime.dylib")]
	[InlineData("prebuilds/win32-x64/runtime.node")]
	[InlineData("tools/claude.cmd")]
	[InlineData("tools/codex.ps1")]
	[InlineData("grok.exe")]
	[InlineData("agy.bat")]
	[InlineData("copilot")]
	[InlineData("node_modules/cli/index.js")]
	[InlineData("unexpected.exe")]
	[InlineData("nested/AiUsageDashboard.App.exe")]
	[InlineData("AiUsageDashboard.Antigravity.Setup.exe")]
	[InlineData("AiUsageDashboard.AntigravityCapture.exe")]
	[InlineData("createdump.exe")]
	[InlineData("third-party-notices/GitHub-Copilot-CLI-LICENSE.md")]
	public async Task PackageCliCheck_RejectsUnexpectedExecutablesAndProviderRuntimeFiles(string relativePath)
	{
		using TemporaryDirectory temporaryDirectory = new();
		string path = Path.Combine(temporaryDirectory.Path, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, "Synthetic package file; never executed.");

		(int exitCode, string output) = await RunPackageCliCheckAsync(temporaryDirectory.Path);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("Unexpected executable or provider CLI package content detected", output, StringComparison.Ordinal);
		Assert.True(File.Exists(path));
	}

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
				"AntigravityLiveR0Profile.cs",
				"AntigravityLiveR0ProfileFile.cs",
				"AntigravityLiveR0Runner.cs",
				"AntigravityLiveR1PrivateCalibrationCaptureModels.cs",
				"AntigravityLiveR1Profile.cs",
				"AntigravityLiveR1RunnerModels.cs",
				"AntigravityLiveR1SectionProfile.cs",
				"AntigravityLiveR1SectionRunnerModels.cs",
				"AntigravityMachineSetupPrivateTransaction.cs",
				"AntigravityObservationGates.cs",
				"AntigravityProductionUsageClient.cs",
				"AntigravityRedactedStructuralCapture.cs",
				"AntigravityReviewedPackageLocalBinding.cs",
				"AntigravityReviewedPackageManifest.cs",
				"AntigravityReviewedPackageManifestCatalog.cs",
				"AntigravityUsageR1SectionCalibration.cs",
				"AssemblyInfo.cs",
				"IConPtySession.cs",
				"WindowsConPtySession.cs"
			},
			productionSourceLinks);
		string[] productionCompileExclusions = productionProject
			.Descendants("Compile")
			.Select(static item => item.Attribute("Remove")?.Value)
			.Where(static remove => !string.IsNullOrWhiteSpace(remove))
			.Select(static remove => Path.GetFileName(remove!))
			.ToArray();
		Assert.Contains("WindowsConPtySession.cs", productionCompileExclusions);
		Assert.Contains("IConPtySession.cs", productionCompileExclusions);
		Assert.Contains("AntigravityLiveR0Runner.cs", productionCompileExclusions);

		string captureProjectPath = Path.Combine(
			repositoryRoot,
			"src",
			"AiUsageDashboard.AntigravityCapture",
			"AiUsageDashboard.AntigravityCapture.csproj");
		Assert.False(File.Exists(captureProjectPath));

		foreach (string consumerPath in new[]
		{
			Path.Combine(repositoryRoot, "AiUsageDashboard.sln"),
			Path.Combine(
				repositoryRoot,
				"src",
				"AiUsageDashboard.App",
				"AiUsageDashboard.App.csproj"),
			Path.Combine(
				repositoryRoot,
				"tests",
				"AiUsageDashboard.Tests",
				"AiUsageDashboard.Tests.csproj")
		})
		{
			Assert.DoesNotContain(
				"AiUsageDashboard.AntigravityCapture",
				File.ReadAllText(consumerPath),
				StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public void PublishScript_EnforcesPinnedSdkAndRuntimeMetadata()
	{
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
			2,
			Regex.Matches(
				script,
				@"(?m)^\tAssert-PublishedProductVersion\s+`\r?$")
				.Count);
		Assert.Contains(
			"-ExecutablePath (Join-Path $appRoot 'AiUsageDashboard.App.exe')",
			script);
		Assert.Contains(
			"-ExecutablePath (Join-Path $appRoot 'AiUsageDashboard.ClaudeCapture.exe')",
			script);
		Assert.Single(Regex.Matches(
			script,
			@"(?m)^\tInvoke-PinnedPublish\s+`\r?$")
			.Cast<Match>());
		Assert.DoesNotContain(
			@"src\AiUsageDashboard.Antigravity.Setup\AiUsageDashboard.Antigravity.Setup.csproj",
			script,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			@"src\AiUsageDashboard.AntigravityCapture\AiUsageDashboard.AntigravityCapture.csproj",
			script,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("$setupPublishRoot", script);
		Assert.DoesNotContain("$antigravityCapturePublishRoot", script);
		Assert.DoesNotContain("Invoke-PinnedTrimmedSingleFilePublish", script);
		Assert.DoesNotContain("Invoke-PublishedCaptureSmokeTest", script);
		Assert.DoesNotContain("Merge-PublishDirectory", script);
		Assert.DoesNotContain("-p:PublishSingleFile=true", script);
		Assert.DoesNotContain("-p:PublishTrimmed=true", script);
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
			Assert.Single(Regex.Matches(
				script,
				Regex.Escape(isolatedBuildFlag)).Cast<Match>());
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
			string expectedVersion = packageName == "System.Text.Json" ? "10.0.12" : "10.0.2";
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
	public void AppProject_EmbedsSetupLibraryWithoutStagingCaptureExecutables()
	{
		string appProjectPath = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"AiUsageDashboard.App.csproj");
		XDocument appProject = XDocument.Load(appProjectPath);
		XElement setupReference = appProject
			.Descendants("ProjectReference")
			.Single(item => string.Equals(
				item.Attribute("Include")?.Value,
				@"..\AiUsageDashboard.Antigravity.Setup\AiUsageDashboard.Antigravity.Setup.csproj",
				StringComparison.Ordinal));
		Assert.Null(setupReference.Attribute("ReferenceOutputAssembly"));
		Assert.Null(setupReference.Attribute("Private"));
		Assert.DoesNotContain(
			appProject.Descendants("ProjectReference"),
			static item => string.Equals(
				item.Attribute("Include")?.Value,
				@"..\AiUsageDashboard.AntigravityCapture\AiUsageDashboard.AntigravityCapture.csproj",
				StringComparison.Ordinal));
		Assert.DoesNotContain(
			appProject.Descendants("Target"),
			static item => string.Equals(
				item.Attribute("Name")?.Value,
				"StageAntigravityCaptureHelper",
				StringComparison.Ordinal));

		XDocument setupProject = XDocument.Load(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.Antigravity.Setup",
			"AiUsageDashboard.Antigravity.Setup.csproj"));
		Assert.DoesNotContain(
			setupProject.Descendants("OutputType"),
			static outputType => string.Equals(
				outputType.Value,
				"WinExe",
				StringComparison.OrdinalIgnoreCase));
		Assert.Contains(
			setupProject.Descendants("UseWPF"),
			static useWpf => string.Equals(
				useWpf.Value,
				"true",
				StringComparison.OrdinalIgnoreCase));
		string setupProjectDirectory = Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.Antigravity.Setup");
		foreach (string standaloneSetupFile in new[]
		{
			"App.xaml",
			"App.xaml.cs",
			"app.manifest",
			"DashboardLauncher.cs"
		})
		{
			Assert.False(File.Exists(Path.Combine(
				setupProjectDirectory,
				standaloneSetupFile)));
		}
	}

	[Fact]
	public void AppProject_RemovesCrashDumpHelperFromPublish()
	{
		XDocument project = XDocument.Load(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"AiUsageDashboard.App.csproj"));
		XElement target = project
			.Descendants("Target")
			.Single(element => string.Equals(
				element.Attribute("Name")?.Value,
				"RemoveCrashDumpToolFromPublish",
				StringComparison.Ordinal));

		Assert.Equal("Publish", target.Attribute("AfterTargets")?.Value);
		Assert.Contains(
			target.Descendants("Delete"),
			element => string.Equals(
				element.Attribute("Files")?.Value,
				"$(PublishDir)createdump.exe",
				StringComparison.Ordinal));
		Assert.Contains(
			target.Descendants("Error"),
			element => string.Equals(
				element.Attribute("Condition")?.Value,
				"Exists('$(PublishDir)createdump.exe')",
				StringComparison.Ordinal));
	}

	[Fact]
	public void ComponentManifest_HasNoStandaloneSetupOrAntigravityCaptureProfile()
	{
		using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(
			Path.Combine(
				RepositoryTestPaths.Root,
				"third-party-notices",
				"component-manifest.json")));

		foreach (JsonElement component in manifest.RootElement
			.GetProperty("components")
			.EnumerateArray())
		{
			string[] profiles = component
				.GetProperty("profiles")
				.EnumerateArray()
				.Select(static value => value.GetString() ?? string.Empty)
				.ToArray();
			Assert.DoesNotContain("setup", profiles);
			Assert.DoesNotContain("capture", profiles);

			string[] deliveryTargets = component
				.GetProperty("deliveryTargets")
				.EnumerateArray()
				.Select(static value => value.GetString() ?? string.Empty)
				.ToArray();
			Assert.DoesNotContain("standalone-capture-exe", deliveryTargets);
		}
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
		Assert.DoesNotContain("GitHub-Copilot-CLI-LICENSE.md", script);
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
	public void PublishScript_AllowsOnlyExpectedExecutablesAndEmbeddedSetupLibrary()
	{
		string script = ReadPublishScript();
		Match requiredFilesMatch = Regex.Match(
			script,
			@"(?ms)^\t\$requiredFiles = @\(\r?\n(?<body>.*?)^\t\)");
		Match forbiddenNamesMatch = Regex.Match(
			script,
			@"(?ms)^\t\$forbiddenNames = @\(\r?\n(?<body>.*?)^\t\)");

		Assert.True(requiredFilesMatch.Success);
		Assert.True(forbiddenNamesMatch.Success);
		string requiredFiles = requiredFilesMatch.Groups["body"].Value;
		string forbiddenNames = forbiddenNamesMatch.Groups["body"].Value;
		Assert.Contains(
			"(Join-Path $appRoot 'AiUsageDashboard.Antigravity.Setup.dll')",
			requiredFiles,
			StringComparison.Ordinal);
		foreach (string forbiddenName in new[]
		{
			"'AiUsageDashboard.Antigravity.Setup.exe'",
			"'AiUsageDashboard.Antigravity.Setup.deps.json'",
			"'AiUsageDashboard.Antigravity.Setup.runtimeconfig.json'",
			"'AiUsageDashboard.AntigravityCapture.exe'",
			"'AiUsageDashboard.AntigravityCapture.dll'",
			"'AiUsageDashboard.AntigravityCapture.deps.json'",
			"'AiUsageDashboard.AntigravityCapture.runtimeconfig.json'"
		})
		{
			Assert.Contains(
				forbiddenName,
				forbiddenNames,
				StringComparison.Ordinal);
		}

		Assert.DoesNotContain(
			"(Join-Path $stagedPackageRoot 'setup')",
			script);
		Assert.DoesNotContain(
			"AiUsageDashboard.Antigravity.Setup.csproj",
			script,
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(
			"AiUsageDashboard.AntigravityCapture.csproj",
			script,
			StringComparison.OrdinalIgnoreCase);
		Assert.Contains(
			"'AiUsageDashboard.AntigravityCapture*.exe'",
			script,
			StringComparison.Ordinal);
		Assert.Contains(
			"'app\\AiUsageDashboard.App.exe'",
			script,
			StringComparison.Ordinal);
		Assert.Contains(
			"'app\\AiUsageDashboard.ClaudeCapture.exe'",
			script,
			StringComparison.Ordinal);
		Assert.Contains(
			"Unexpected executable detected:",
			script,
			StringComparison.Ordinal);

		Match forbiddenEnumeration = Regex.Match(
			script,
			@"(?ms)\$forbidden = @\(Get-ChildItem `\r?\n(?<body>.*?)\|\r?\n\s*Where-Object");
		Match executableEnumeration = Regex.Match(
			script,
			@"(?ms)\$unexpectedExecutables = @\(Get-ChildItem `\r?\n(?<body>.*?)\|\r?\n\s*Where-Object");
		Assert.True(forbiddenEnumeration.Success);
		Assert.True(executableEnumeration.Success);
		Assert.Contains(
			"-Force `",
			forbiddenEnumeration.Groups["body"].Value,
			StringComparison.Ordinal);
		Assert.Contains(
			"-Force `",
			executableEnumeration.Groups["body"].Value,
			StringComparison.Ordinal);
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
	public void DistributionDocs_DoNotDescribeStandaloneSetupExecutable()
	{
		string repositoryRoot = RepositoryTestPaths.Root;
		string technicalOverview = File.ReadAllText(Path.Combine(
			repositoryRoot,
			"docs",
			"TECHNICAL_OVERVIEW.md"));
		string distributionGuide = File.ReadAllText(
			Path.Combine(repositoryRoot, "INTERNAL_DISTRIBUTION.md"));

		foreach (string document in new[]
		{
			technicalOverview,
			distributionGuide
		})
		{
			Assert.DoesNotContain(
				@"app\AiUsageDashboard.Antigravity.Setup.exe",
				document,
				StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(
				@"setup\AiUsageDashboard.Antigravity.Setup.exe",
				document,
				StringComparison.OrdinalIgnoreCase);
		}

		Assert.Contains(
			"套件不再包含 `AiUsageDashboard.Antigravity.Setup.exe`",
			distributionGuide,
			StringComparison.Ordinal);
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

		foreach ((string platform, string accountCards) in new[]
		{
			("Claude", "多帳號；同帳號依不同組織分卡"),
			("Codex", "多帳號；同帳號依不同工作區分卡"),
			("GitHub Copilot", "多帳號；同帳號限一張卡片"),
			("Grok", "多帳號；同帳號限一張卡片"),
			("Antigravity", "限一個帳號")
		})
		{
			string rowPattern =
				$@"(?m)^\| {Regex.Escape(platform)} \|[^\r\n|]*\| {Regex.Escape(accountCards)} \|\r?$";
			Assert.Matches(rowPattern, readme);
			Assert.Matches(rowPattern, userGuide);
		}
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
		string copilotRequirements = Regex.Match(
			userGuide,
			@"(?m)^\| GitHub Copilot \| (?<requirements>[^\r\n|]*)\|")
			.Groups["requirements"].Value;
		Assert.Contains("安裝", copilotRequirements, StringComparison.Ordinal);
		Assert.Contains("Copilot CLI", copilotRequirements, StringComparison.Ordinal);
		Assert.Contains("`github.com`", copilotRequirements, StringComparison.Ordinal);
		Assert.Contains("https://docs.github.com/en/copilot/", copilotRequirements, StringComparison.Ordinal);
	}

	[Fact]
	public void CliVersionGuides_DistinguishVersionRequirementsFromDiagnosticBaselines()
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

		Assert.Contains(
			"0.144.1",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"1.0.3",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"`>=2.1.169`",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"1.1.11 以上、2.0.0 未滿",
			userGuide,
			StringComparison.Ordinal);
		Assert.Contains(
			"不是最低版本門檻",
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
	public void AntigravitySetup_RunsAsInProcessWindowWithoutExecutableLaunchProtocol()
	{
		string setupWindowSource = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.Antigravity.Setup",
			"SetupWindow.xaml.cs"));
		string launcherSource = File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"src",
			"AiUsageDashboard.App",
			"Providers",
			"AntigravityAccountSetupLauncher.cs"));

		Assert.Contains(
			"TaskCompletionSource<AntigravitySetupDialogResult>",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"internal Task<AntigravitySetupDialogResult> Completion",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"protected override void OnClosed(EventArgs e)",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"_completionSource.TrySetResult(",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Application.Current.Shutdown",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Environment.ExitCode",
			setupWindowSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"DashboardManagedResultProtocolEnvironmentVariable",
			setupWindowSource,
			StringComparison.Ordinal);

		Assert.Contains(
			"window.Show();",
			launcherSource,
			StringComparison.Ordinal);
		Assert.Contains(
			"return await window.Completion;",
			launcherSource,
			StringComparison.Ordinal);
		Assert.DoesNotContain("window.ShowDialog", launcherSource);
		Assert.DoesNotContain("ProcessStartInfo", launcherSource);
		Assert.DoesNotContain("Process.Start", launcherSource);
		Assert.DoesNotContain(
			"AiUsageDashboard.Antigravity.Setup.exe",
			launcherSource,
			StringComparison.OrdinalIgnoreCase);
	}

	private static string ReadPublishScript()
	{
		return File.ReadAllText(Path.Combine(
			RepositoryTestPaths.Root,
			"tools",
			"Publish-Internal.ps1"));
	}

	private static async Task<(int ExitCode, string Output)> RunPackageCliCheckAsync(string appRoot)
	{
		ProcessStartInfo startInfo = new("powershell.exe")
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = appRoot
		};
		foreach (string argument in new[]
		{
			"-NoLogo", "-NoProfile", "-NonInteractive", "-File",
			Path.Combine(RepositoryTestPaths.Root, "tools", "Assert-NoBundledProviderCli.ps1"),
			"-AppRoot", appRoot
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
		return (process.ExitCode, outputTask.Result + errorTask.Result);
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
