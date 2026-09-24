namespace AiUsageDashboard.Tests;

public sealed class UpdaterPackageInvariantsTests
{
	[Fact]
	public void UpdaterProject_IsStandaloneAndReferencesOnlyUpdaterCore()
	{
		string project = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"AiUsageDashboard.Updater.csproj");

		Assert.Contains("<OutputType>Exe</OutputType>", project);
		Assert.Contains("<TargetFramework>net8.0-windows</TargetFramework>", project);
		Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>", project);
		Assert.Contains(
			@"..\AiUsageDashboard.Updater.Core\AiUsageDashboard.Updater.Core.csproj",
			project);
		Assert.DoesNotContain("PackageReference", project);
	}

	[Theory]
	[InlineData("Publish-Internal.ps1")]
	[InlineData("Publish-Updater.ps1")]
	[InlineData("Publish-UpdateBundle.ps1")]
	public void ProductionPublishEntryPoints_RequirePowerShell74(
		string scriptName)
	{
		string script = ReadRepositoryFile("tools", scriptName);

		Assert.StartsWith("#Requires -Version 7.4", script);
	}

	[Fact]
	public void AppProductionPublishEntryPoints_RequireSignedFeedMetadata()
	{
		string appProject = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.App",
			"AiUsageDashboard.App.csproj");
		string internalPublish = ReadRepositoryFile(
			"tools",
			"Publish-Internal.ps1");
		string bundlePublish = ReadRepositoryFile(
			"tools",
			"Publish-UpdateBundle.ps1");
		string windowsCi = ReadRepositoryFile(
			".github",
			"workflows",
			"windows-ci.yml");

		Assert.Contains("RequireUpdateFeedMetadataForPublish", appProject);
		Assert.Contains("Publishing the app requires UpdateFeedUrl.", appProject);
		Assert.Contains("Publishing the app requires UpdateChannel.", appProject);
		Assert.Contains("UpdateTrustedKeysFile", appProject);
		Assert.Contains("-p:UpdateFeedUrl=$FeedUrl", internalPublish);
		Assert.Contains("-p:UpdateChannel=$Channel", internalPublish);
		Assert.Contains(
			"-p:UpdateTrustedKeysFile=$resolvedTrustedKeysFile",
			internalPublish);
		Assert.Contains("$parsedFeedUri.Host", internalPublish);
		Assert.Contains("validate-trust", internalPublish);
		Assert.Contains(
			"function Assert-PublishedAppUpdateConfiguration",
			internalPublish);
		Assert.Contains(
			"GetCustomAttributes(",
			internalPublish);
		Assert.Contains(
			"[Reflection.AssemblyMetadataAttribute]",
			internalPublish);
		Assert.Contains(
			"GetManifestResourceStream($resourceName)",
			internalPublish);
		Assert.Contains(
			"StructuralEqualityComparer.Equals(",
			internalPublish);
		Assert.Contains(
			"-ExpectedFeedUrl $FeedUrl",
			internalPublish);
		Assert.Contains(
			"-ExpectedChannel $Channel",
			internalPublish);
		Assert.Contains(
			"-ExpectedTrustedKeysFile $resolvedTrustedKeysFile",
			internalPublish);
		Assert.Contains("-FeedUrl $FeedUrl", bundlePublish);
		Assert.Contains("-Channel $Channel", bundlePublish);
		Assert.Contains(
			"-TrustedKeysFile $resolvedTrustedKeysFile",
			bundlePublish);
		Assert.Contains("./tools/Publish-Internal.ps1", windowsCi);
		Assert.Contains("-FeedUrl 'https://updates.example.test/ci/stable.json'", windowsCi);
		Assert.Contains("-Channel 'ci'", windowsCi);
		Assert.Contains("-TrustedKeysFile $trustPath", windowsCi);
	}

	[Fact]
	public void PublishUpdaterScript_IsBomEncodedAndPinsSingleFileRuntime()
	{
		string scriptPath = Path.Combine(
			RepositoryTestPaths.Root,
			"tools",
			"Publish-Updater.ps1");
		byte[] bytes = File.ReadAllBytes(scriptPath);
		string script = File.ReadAllText(scriptPath);

		Assert.True(bytes.Length >= 3);
		Assert.Equal(0xEF, bytes[0]);
		Assert.Equal(0xBB, bytes[1]);
		Assert.Equal(0xBF, bytes[2]);
		Assert.Contains("$requiredSdkVersion = '8.0.425'", script);
		Assert.Contains("$selfContainedRuntimeVersion = '8.0.31'", script);
		Assert.Contains("$runtimeIdentifier = 'win-x64'", script);
		Assert.Contains("-p:RuntimeFrameworkVersion=$selfContainedRuntimeVersion", script);
		Assert.Contains("-p:TargetLatestRuntimePatch=false", script);
		Assert.Contains("-p:PublishSingleFile=true", script);
		Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", script);
		Assert.Contains("-p:EnableCompressionInSingleFile=true", script);
		Assert.Contains("-p:DebugSymbols=false", script);
		Assert.Contains("AiUsageDashboard.Updater.exe", script);
		Assert.DoesNotContain("dotnet tool install", script);
		Assert.DoesNotContain("winget", script, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("choco", script, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void PublishUpdaterScript_ProtectsFormalAndDirtyVerificationOutputs()
	{
		string script = ReadRepositoryFile("tools", "Publish-Updater.ps1");

		Assert.Contains(
			"Formal updater packages require a clean Git working tree.",
			script);
		Assert.Contains("$AllowDirtyTreeForVerification", script);
		Assert.Contains("$Version.Contains(", script);
		Assert.Contains("'verify'", script);
		Assert.Contains("$defaultOutputRootPrefix", script);
		Assert.Contains("Refusing to clean an updater tree", script);
		Assert.Contains("[IO.FileAttributes]::ReparsePoint", script);
		Assert.Contains("[System.IO.File]::Move", script);
		Assert.Contains("Get-FileHash", script);
		Assert.Contains("-Algorithm SHA256", script);
	}

	[Fact]
	public void PublishScripts_UseRuntimeCompatibleSemVerAndCandidateDefaults()
	{
		string updaterScript = ReadRepositoryFile(
			"tools",
			"Publish-Updater.ps1");
		string bundleScript = ReadRepositoryFile(
			"tools",
			"Publish-UpdateBundle.ps1");
		string workflow = ReadRepositoryFile(
			".github",
			"workflows",
			"internal-release-candidate.yml");

		Assert.Contains(
			"[Management.Automation.SemanticVersion]::Parse($Version)",
			updaterScript);
		Assert.Contains(
			"[Management.Automation.SemanticVersion]::Parse($Version)",
			bundleScript);
		Assert.Contains(
			"$MinimumUpdaterVersion = if ([string]::IsNullOrWhiteSpace(",
			bundleScript);
		Assert.Contains(
			"MinimumUpdaterVersion cannot exceed Version.",
			bundleScript);
		Assert.Contains("AiUsageDashboard.FeedSigning", bundleScript);
		Assert.Contains("--private-key-file $resolvedPrivateKeyFile", bundleScript);
		Assert.Contains("--feed $feedPath --trust $resolvedTrustedKeysFile", bundleScript);
		Assert.Contains("$ReleaseSequence -le $PreviousReleaseSequence", bundleScript);
		Assert.Contains("$candidateVersion = $version", workflow);
		Assert.Contains("-ReleaseSequence $env:RELEASE_SEQUENCE", workflow);
		Assert.DoesNotContain("-ReleaseSequence $env:GITHUB_RUN_NUMBER", workflow);
		Assert.Contains("-PreviousReleaseSequence $env:PREVIOUS_RELEASE_SEQUENCE", workflow);
		Assert.Contains("UPDATE_FEED_SIGNING_PRIVATE_KEY_PEM", workflow);
		Assert.Contains("-Channel stable", workflow);
		Assert.Contains("AiUsageDashboard-update-stable.json", workflow);
		Assert.Contains(
			"$feed.minimumUpdaterVersion -cne $expectedUpdaterVersion",
			workflow);
		Assert.DoesNotContain(
			"$version.g$shortSha.run$env:GITHUB_RUN_ID",
			workflow);
		Assert.Contains(
			"publish_github_prerelease:",
			workflow);
		Assert.Contains(
			"if: ${{ inputs.publish_github_prerelease }}",
			workflow);
		Assert.Contains("actions: read", workflow);
		Assert.Contains("contents: write", workflow);
		Assert.Contains("gh release create $tag @assets", workflow);
		Assert.DoesNotContain("--draft=false", workflow);
		Assert.Contains("--draft", workflow);
		Assert.Contains("candidate-freeze.json", workflow);
		Assert.Contains(
			"$remoteAsset[0].digest -cne $localDigest",
			workflow);
	}

	[Fact]
	public void ProcessGate_UsesQueryReserveAndNaturalWaitWithoutTermination()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"RunningPayloadProcessGate.cs");
		int queryIndex = source.IndexOf(
			"_shutdownClient.QueryAsync(",
			StringComparison.Ordinal);
		int reserveIndex = source.IndexOf(
			"_shutdownClient.ReserveAsync(",
			StringComparison.Ordinal);
		int waitIndex = source.IndexOf(
			"WaitForNaturalExitAsync(",
			reserveIndex,
			StringComparison.Ordinal);

		Assert.True(queryIndex >= 0);
		Assert.True(reserveIndex > queryIndex);
		Assert.True(waitIndex > reserveIndex);
		Assert.Contains("ProcessStartTimeUtcTicks", source);
		Assert.Contains("PayloadGenerationId", source);
		Assert.Contains("_processProbe.Capture(resolvedPayloadRoot)", source);
		Assert.DoesNotContain(".Kill(", source, StringComparison.Ordinal);
		Assert.DoesNotContain("TerminateProcess", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Program_PreflightsElevationAndHoldsAppMutexAcrossFinalScanAndSwitch()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"Program.cs");
		int elevationIndex = source.IndexOf(
			"UpdateExecutionSafety.EnsureNotElevated();",
			StringComparison.Ordinal);
		int lockIndex = source.IndexOf(
			"UpdateInstallLock.Acquire(",
			StringComparison.Ordinal);
		int quiescentIndex = source.IndexOf(
			"await EnsureCurrentInstallationIsQuiescentAsync(",
			StringComparison.Ordinal);
		int mutexIndex = source.IndexOf(
			"AppSingleInstanceMutexLease.Acquire(",
			StringComparison.Ordinal);
		int finalScanIndex = source.IndexOf(
			"processGate.EnsureNoPayloadProcesses(",
			StringComparison.Ordinal);
		int switchIndex = source.IndexOf(
			"new UpdateInstallationSwitcher().Switch(transaction);",
			StringComparison.Ordinal);
		int restartIndex = source.IndexOf(
			"StartApplication(executablePath);",
			StringComparison.Ordinal);

		Assert.True(elevationIndex >= 0);
		Assert.True(lockIndex > elevationIndex);
		Assert.True(quiescentIndex > lockIndex);
		Assert.True(mutexIndex > quiescentIndex);
		Assert.True(finalScanIndex > mutexIndex);
		Assert.True(switchIndex > finalScanIndex);
		Assert.True(restartIndex > switchIndex);
	}

	[Fact]
	public void Program_RejectsPayloadReplayBeforeDelegatingToDownloadedUpdater()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"Program.cs");
		int payloadPreflightIndex = source.IndexOf(
			"OnlinePayloadUpdatePolicy.Evaluate(",
			StringComparison.Ordinal);
		int updaterRefreshIndex = source.IndexOf(
			"UpdaterRefreshPolicy.Evaluate(",
			StringComparison.Ordinal);
		int delegatedStartIndex = source.IndexOf(
			"RunDelegatedUpdaterAsync(",
			updaterRefreshIndex,
			StringComparison.Ordinal);

		Assert.True(payloadPreflightIndex >= 0);
		Assert.True(updaterRefreshIndex > payloadPreflightIndex);
		Assert.True(delegatedStartIndex > updaterRefreshIndex);
	}

	[Fact]
	public void Program_DelegatedChildSchedulesPromotionOnlyAfterRegistrationCompletes()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"Program.cs");
		int signedUpdaterValidationIndex = source.IndexOf(
			"UpdaterRefreshPolicy.EnsureMatchesSignedInstaller(",
			StringComparison.Ordinal);
		int updateResultIndex = source.IndexOf(
			"UpdaterExecutionResult updateResult = await ApplyPackageAsync(",
			signedUpdaterValidationIndex,
			StringComparison.Ordinal);
		int promotionIndex = source.IndexOf(
			"await CompleteDelegatedMaintenanceUpdaterPromotionAsync(",
			updateResultIndex,
			StringComparison.Ordinal);
		int alreadyCurrentResultIndex = source.IndexOf(
			"currentResult = await RegisterAndRestartCurrentAsync(",
			signedUpdaterValidationIndex,
			StringComparison.Ordinal);
		int alreadyCurrentPromotionIndex = source.IndexOf(
			"await CompleteDelegatedMaintenanceUpdaterPromotionAsync(",
			alreadyCurrentResultIndex,
			StringComparison.Ordinal);

		Assert.True(signedUpdaterValidationIndex >= 0);
		Assert.True(updateResultIndex > signedUpdaterValidationIndex);
		Assert.True(promotionIndex > updateResultIndex);
		Assert.True(alreadyCurrentResultIndex > signedUpdaterValidationIndex);
		Assert.True(alreadyCurrentPromotionIndex > alreadyCurrentResultIndex);
		Assert.Contains(
			"result.ExitCode is not SuccessExitCode and not RestartFailureExitCode",
			source);
		string coordinator = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"DelegatedMaintenanceUpdaterPromotionCoordinator.cs");
		Assert.Contains("UpdaterArtifactCache.GetExecutablePath(", coordinator);
		Assert.Contains("lineage.ParentExecutablePath", coordinator);
		Assert.Contains("GetMaintenanceUpdaterGeneration(", coordinator);
		Assert.Contains("_promotionLauncher.LaunchAsync(", coordinator);
	}

	[Fact]
	public void Program_AlreadyCurrentPromotion_ReleasesInstallLockAfterRegistration()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"Program.cs").Replace("\r\n", "\n", StringComparison.Ordinal);
		int branchStart = source.IndexOf(
			"if (preflightAction == OnlinePayloadUpdateAction.UseInstalled)",
			StringComparison.Ordinal);
		Assert.True(branchStart >= 0);
		int branchEnd = source.IndexOf(
			"UpdateDownloadWorkspace workspace =",
			branchStart,
			StringComparison.Ordinal);
		Assert.True(branchEnd > branchStart);
		string branch = source[branchStart..branchEnd];
		int lockStart = branch.IndexOf(
			"using (UpdateInstallLock updateLock = UpdateInstallLock.Acquire(",
			StringComparison.Ordinal);
		Assert.True(lockStart >= 0);
		int recoveryIndex = branch.IndexOf(
			"RecoverInterruptedTransactionsWhileLocked(options.InstallRoot);",
			lockStart,
			StringComparison.Ordinal);
		Assert.True(recoveryIndex > lockStart);
		int manifestIndex = branch.IndexOf(
			"await ReadInstalledManifestAsync(",
			recoveryIndex,
			StringComparison.Ordinal);
		Assert.True(manifestIndex > recoveryIndex);
		int policyIndex = branch.IndexOf(
			"OnlinePayloadUpdatePolicy.Evaluate(",
			manifestIndex,
			StringComparison.Ordinal);
		Assert.True(policyIndex > manifestIndex);
		int registrationIndex = branch.IndexOf(
			"currentResult = await RegisterAndRestartCurrentAsync(",
			policyIndex,
			StringComparison.Ordinal);
		Assert.True(registrationIndex > policyIndex);
		int lockEnd = branch.IndexOf(
			"\n\t\t\t}\n\n\t\t\tif (currentResult is not null)",
			registrationIndex,
			StringComparison.Ordinal);
		Assert.True(lockEnd > registrationIndex);
		int promotionIndex = branch.IndexOf(
			"return await CompleteDelegatedMaintenanceUpdaterPromotionAsync(",
			lockEnd,
			StringComparison.Ordinal);
		Assert.True(promotionIndex > lockEnd);
	}

	[Fact]
	public void Promotion_HoldsInstallLockAcrossFinalValidationAndAtomicReplace()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"MaintenanceUpdaterPromotion.cs");
		int parentExitIndex = source.IndexOf(
			"await _processExitWaiter.WaitForExitAsync(",
			StringComparison.Ordinal);
		int lockIndex = source.IndexOf(
			"UpdateInstallLock.AcquireExisting(",
			parentExitIndex,
			StringComparison.Ordinal);
		int finalValidationIndex = source.IndexOf(
			"await ValidateSourceAndCanonicalAsync(",
			lockIndex,
			StringComparison.Ordinal);
		int replaceIndex = source.IndexOf(
			"File.Move(temporaryPath, canonicalPath, overwrite: true)",
			finalValidationIndex,
			StringComparison.Ordinal);

		Assert.True(parentExitIndex >= 0);
		Assert.True(lockIndex > parentExitIndex);
		Assert.True(finalValidationIndex > lockIndex);
		Assert.True(replaceIndex > finalValidationIndex);
	}

	[Fact]
	public void PromotionLauncher_StartsGenerationBeforeReleasingInstallLock()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"MaintenanceUpdaterPromotionLauncher.cs");
		int lockIndex = source.IndexOf(
			"using (UpdateInstallLock updateLock = UpdateInstallLock.AcquireExisting(",
			StringComparison.Ordinal);
		int receiptIndex = source.IndexOf(
			"bool receiptWasSaved = expectedReceipt is null",
			lockIndex,
			StringComparison.Ordinal);
		int processStartIndex = source.IndexOf(
			"Process.Start(startInfo)",
			receiptIndex,
			StringComparison.Ordinal);
		int failureReceiptIndex = source.IndexOf(
			"SaveFailureIfCurrentAsync(",
			processStartIndex,
			StringComparison.Ordinal);

		Assert.True(lockIndex >= 0);
		Assert.True(receiptIndex > lockIndex);
		Assert.True(processStartIndex > receiptIndex);
		Assert.True(failureReceiptIndex > processStartIndex);
	}

	[Fact]
	public void Program_PropagatesIncompleteMaintenancePromotionAsFailure()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"Program.cs");

		Assert.Contains(
			"private const int MaintenanceUpdaterIncompleteExitCode = 4;",
			source);
		Assert.Contains("ApplyMaintenanceUpdaterWarning(", source);
		Assert.Contains(
			"? MaintenanceUpdaterIncompleteExitCode",
			source);
		Assert.Contains(
			"TryRetryPendingMaintenanceUpdaterPromotionAsync(",
			source);
	}

	[Fact]
	public void Program_RetriesPersistedPromotionBeforeFetchingFutureFeed()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"Program.cs");
		int retryIndex = source.IndexOf(
			"await TryRetryPendingMaintenanceUpdaterPromotionAsync(",
			StringComparison.Ordinal);
		int feedFetchIndex = source.IndexOf(
			"await feedClient.FetchFeedAsync(",
			retryIndex,
			StringComparison.Ordinal);

		Assert.True(retryIndex >= 0);
		Assert.True(feedFetchIndex > retryIndex);
		string receiptStore = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"MaintenanceUpdaterPromotionReceiptStore.cs");
		Assert.Contains("FileOptions.WriteThrough", receiptStore);
		Assert.Contains("stream.Flush(flushToDisk: true)", receiptStore);
		Assert.Contains("DeleteIfMatchesAsync", receiptStore);
		Assert.Contains(
			"SavePendingIfSnapshotMatchesWhileInstallLockHeldAsync",
			receiptStore);
		Assert.Contains("LaunchRetryAsync(", source);
	}

	[Fact]
	public void Program_RegistersOnlyAfterSwitchAndBeforeRestart()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"Program.cs");
		int switchIndex = source.IndexOf(
			"new UpdateInstallationSwitcher().Switch(transaction);",
			StringComparison.Ordinal);
		int registrationIndex = source.IndexOf(
			"await TryRegisterInstalledAppAsync(",
			switchIndex,
			StringComparison.Ordinal);
		int restartIndex = source.IndexOf(
			"StartApplication(executablePath);",
			registrationIndex,
			StringComparison.Ordinal);

		Assert.True(switchIndex >= 0);
		Assert.True(registrationIndex > switchIndex);
		Assert.True(restartIndex > registrationIndex);
		Assert.True(
			source.Split("await RegisterAndRestartCurrentAsync(").Length >= 3);
	}

	[Fact]
	public void Uninstaller_UsesOwnedAllowlistAndNonRecursiveRootDeletion()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"ManagedInstallationUninstaller.cs");

		Assert.Contains("\"current\"", source);
		Assert.Contains("\"transactions\"", source);
		Assert.Contains("\"downloads\"", source);
		Assert.Contains("\"updater-cache\"", source);
		Assert.Contains("FileAttributes.ReparsePoint", source);
		Assert.Contains("Directory.Delete(installRoot, recursive: false)", source);
		Assert.DoesNotContain("Directory.Delete(installRoot, recursive: true)", source);
		Assert.DoesNotContain(".Kill(", source, StringComparison.Ordinal);
		Assert.DoesNotContain("TerminateProcess", source, StringComparison.Ordinal);
	}

	[Fact]
	public void CommandLineContract_RequiresLocalPackageAndChecksum()
	{
		string source = ReadRepositoryFile(
			"src",
			"AiUsageDashboard.Updater",
			"UpdaterCommandLine.cs");

		Assert.Contains("apply-local", source);
		Assert.Contains("--package", source);
		Assert.Contains("--sha256", source);
		Assert.Contains("--install-root", source);
		Assert.Contains("--no-restart", source);
		Assert.Contains("LocalApplicationData", source);
		Assert.Contains(
			"WindowsLogonStartupRegistrationContract",
			source);
		Assert.Contains("uninstall --confirm", source);
		Assert.Contains(
			"MaintenanceUpdaterPathContract",
			source);
		Assert.DoesNotContain("\"AiUsageDashboardUpdater\"", source);
	}

	private static string ReadRepositoryFile(params string[] pathParts)
	{
		return File.ReadAllText(Path.Combine(
			[RepositoryTestPaths.Root, .. pathParts]));
	}
}
