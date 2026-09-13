[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
	[string] $Version,

	[string] $OutputRoot,

	[switch] $AllowDirtyTreeForVerification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredSdkVersion = '8.0.425'
$selfContainedRuntimeVersion = '8.0.31'
$runtimeIdentifier = 'win-x64'
$repositoryRoot = [System.IO.Path]::GetFullPath(
	(Join-Path $PSScriptRoot '..'))
$defaultOutputRoot = [System.IO.Path]::GetFullPath(
	(Join-Path $repositoryRoot 'publish'))
$defaultOutputRootPrefix =
	$defaultOutputRoot + [System.IO.Path]::DirectorySeparatorChar
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
	$defaultOutputRoot
}
else {
	$OutputRoot
}
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$resolvedOutputRootPrefix =
	$resolvedOutputRoot.TrimEnd(
		[System.IO.Path]::DirectorySeparatorChar,
		[System.IO.Path]::AltDirectorySeparatorChar) +
	[System.IO.Path]::DirectorySeparatorChar
$archiveRootName = 'AiUsageDashboard'
$packageName = "AiUsageDashboard-$Version-win-x64"
$packageRoot = Join-Path $resolvedOutputRoot $packageName
$zipPath = Join-Path $resolvedOutputRoot "$packageName.zip"
$checksumPath = "$zipPath.sha256"
$workingRoot = Join-Path `
	-Path $resolvedOutputRoot `
	-ChildPath ".aud-build-$([Guid]::NewGuid().ToString('N'))"
$stagedPackageRoot = Join-Path $workingRoot $archiveRootName
$appRoot = Join-Path $stagedPackageRoot 'app'
$setupPublishRoot = Join-Path $workingRoot 'setup-publish'
$antigravityCapturePublishRoot = Join-Path `
	$workingRoot `
	'antigravity-capture-publish'
$temporaryZipPath = Join-Path $workingRoot "$packageName.zip"
$temporaryChecksumPath = "$temporaryZipPath.sha256"
$packageRootFinalized = $false
$zipFinalized = $false
$checksumFinalized = $false
$locationPushed = $false
$sourceRevisionId = ''

function Assert-PublishedRuntime {
	param(
		[Parameter(Mandatory = $true)]
		[string] $PublishRoot,

		[Parameter(Mandatory = $true)]
		[string] $AssemblyName
	)

	$runtimeConfigPath = Join-Path `
		-Path $PublishRoot `
		-ChildPath "$AssemblyName.runtimeconfig.json"
	$depsPath = Join-Path `
		-Path $PublishRoot `
		-ChildPath "$AssemblyName.deps.json"

	if (!(Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) -or
		!(Test-Path -LiteralPath $depsPath -PathType Leaf)) {
		throw "Runtime metadata is missing for $AssemblyName."
	}

	$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw |
		ConvertFrom-Json
	$runtimeOptions = $runtimeConfig.runtimeOptions
	$frameworksProperty =
		$runtimeOptions.PSObject.Properties['includedFrameworks']
	if ($null -eq $frameworksProperty) {
		$frameworksProperty =
			$runtimeOptions.PSObject.Properties['frameworks']
	}

	if ($null -ne $frameworksProperty) {
		$frameworks = @($frameworksProperty.Value)
	}
	else {
		$frameworkProperty = $runtimeOptions.PSObject.Properties['framework']
		$frameworks = if ($null -eq $frameworkProperty) {
			@()
		}
		else {
			@($frameworkProperty.Value)
		}
	}
	$expectedFrameworks = @(
		'Microsoft.NETCore.App',
		'Microsoft.WindowsDesktop.App'
	)

	foreach ($frameworkName in $expectedFrameworks) {
		$matchingFrameworks = @($frameworks | Where-Object {
			[string]::Equals(
				$_.name,
				$frameworkName,
				[StringComparison]::Ordinal)
		})

		if (($matchingFrameworks.Count -ne 1) -or
			!([string]::Equals(
				$matchingFrameworks[0].version,
				$selfContainedRuntimeVersion,
				[StringComparison]::Ordinal))) {
			throw (
				"$AssemblyName runtimeconfig does not pin " +
				"$frameworkName to $selfContainedRuntimeVersion.")
		}
	}

	$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
	$libraryNames = @($deps.libraries.PSObject.Properties.Name)
	$expectedRuntimePacks = @(
		"runtimepack.Microsoft.NETCore.App.Runtime.$runtimeIdentifier/$selfContainedRuntimeVersion"
		"runtimepack.Microsoft.WindowsDesktop.App.Runtime.$runtimeIdentifier/$selfContainedRuntimeVersion"
	)

	foreach ($runtimePack in $expectedRuntimePacks) {
		if (!($libraryNames -contains $runtimePack)) {
			throw (
				"$AssemblyName deps.json does not contain required " +
				"runtime pack $runtimePack.")
		}
	}
}

function Assert-PublishedProductVersion {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ExecutablePath
	)

	if (!(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
		throw "Published executable is missing: $ExecutablePath"
	}

	$expectedProductVersion = "$Version+$sourceRevisionId"
	$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
		$ExecutablePath).ProductVersion

	if (!([string]::Equals(
		$productVersion,
		$expectedProductVersion,
		[StringComparison]::Ordinal))) {
		throw (
			"Published executable ProductVersion '$productVersion' does not " +
			"match expected '$expectedProductVersion': $ExecutablePath")
	}
}

function Invoke-PinnedPublish {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ProjectPath,

		[Parameter(Mandatory = $true)]
		[string] $PublishRoot,

		[Parameter(Mandatory = $true)]
		[string] $DisplayName
	)

	& dotnet publish `
		$ProjectPath `
		--configuration Release `
		--runtime $runtimeIdentifier `
		--self-contained true `
		--disable-build-servers `
		-m:1 `
		-nodeReuse:false `
		-p:UseSharedCompilation=false `
		-p:RuntimeFrameworkVersion=$selfContainedRuntimeVersion `
		-p:TargetLatestRuntimePatch=false `
		-p:Version=$Version `
		-p:SourceRevisionId=$sourceRevisionId `
		-p:DebugType=None `
		-p:DebugSymbols=false `
		--output $PublishRoot
	$publishSucceeded = $?
	$publishExitCode = $global:LASTEXITCODE

	if (!$publishSucceeded -or ($publishExitCode -ne 0)) {
		throw (
			"$DisplayName publish failed with exit code $publishExitCode. " +
			"Install SDK $requiredSdkVersion and ensure the " +
			"$selfContainedRuntimeVersion $runtimeIdentifier runtime packs " +
			"can be restored.")
	}
}

function Invoke-PinnedTrimmedSingleFilePublish {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ProjectPath,

		[Parameter(Mandatory = $true)]
		[string] $PublishRoot,

		[Parameter(Mandatory = $true)]
		[string] $DisplayName,

		[Parameter(Mandatory = $true)]
		[string] $ExecutableName
	)

	& dotnet publish `
		$ProjectPath `
		--configuration Release `
		--runtime $runtimeIdentifier `
		--self-contained true `
		--disable-build-servers `
		-m:1 `
		-nodeReuse:false `
		-p:UseSharedCompilation=false `
		-p:RuntimeFrameworkVersion=$selfContainedRuntimeVersion `
		-p:TargetLatestRuntimePatch=false `
		-p:Version=$Version `
		-p:SourceRevisionId=$sourceRevisionId `
		-p:PublishSingleFile=true `
		-p:IncludeNativeLibrariesForSelfExtract=true `
		-p:EnableCompressionInSingleFile=true `
		-p:PublishTrimmed=true `
		-p:TrimMode=full `
		-p:SuppressTrimAnalysisWarnings=false `
		-p:DebugType=None `
		-p:DebugSymbols=false `
		--output $PublishRoot
	$publishSucceeded = $?
	$publishExitCode = $global:LASTEXITCODE

	if (!$publishSucceeded -or ($publishExitCode -ne 0)) {
		throw (
			"$DisplayName publish failed with exit code $publishExitCode. " +
			"Install SDK $requiredSdkVersion and ensure the " +
			"$selfContainedRuntimeVersion $runtimeIdentifier runtime packs " +
			"can be restored.")
	}

	$publishedFiles = @(Get-ChildItem `
		-LiteralPath $PublishRoot `
		-Recurse `
		-File `
		-Force)
	$expectedExecutablePath = Join-Path $PublishRoot $ExecutableName

	if (($publishedFiles.Count -ne 1) -or
		!([string]::Equals(
			$publishedFiles[0].FullName,
			$expectedExecutablePath,
			[StringComparison]::OrdinalIgnoreCase))) {
		$publishedNames = @($publishedFiles | ForEach-Object {
			[System.IO.Path]::GetRelativePath(
				$PublishRoot,
				$_.FullName)
		})
		throw (
			"$DisplayName must publish as exactly one executable named " +
			"$ExecutableName. Published: $($publishedNames -join ', ')")
	}
}

function Invoke-PublishedCaptureSmokeTest {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ExecutablePath
	)

	$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = $ExecutablePath
	$startInfo.UseShellExecute = $false
	$startInfo.CreateNoWindow = $true
	$startInfo.RedirectStandardOutput = $true
	$startInfo.RedirectStandardError = $true
	[void] $startInfo.ArgumentList.Add(
		'--ai-usage-dashboard-agy-statusline-smoke-test-v1')
	$process = [System.Diagnostics.Process]::new()
	$process.StartInfo = $startInfo
	$started = $false

	try {
		$started = $process.Start()

		if (!$started) {
			throw 'Published Antigravity capture smoke test did not start.'
		}

		$stdoutTask = $process.StandardOutput.ReadToEndAsync()
		$stderrTask = $process.StandardError.ReadToEndAsync()

		if (!$process.WaitForExit(10000)) {
			$process.Kill($true)
			$process.WaitForExit()
			throw 'Published Antigravity capture smoke test timed out.'
		}

		$process.WaitForExit()
		$stdout = $stdoutTask.GetAwaiter().GetResult()
		$stderr = $stderrTask.GetAwaiter().GetResult()

		if (($process.ExitCode -ne 0) -or
			!([string]::IsNullOrEmpty($stdout)) -or
			!([string]::IsNullOrEmpty($stderr))) {
			throw (
				'Published Antigravity capture smoke test failed with exit ' +
				"code $($process.ExitCode).")
		}
	}
	finally {
		if ($started -and !$process.HasExited) {
			$process.Kill($true)
			$process.WaitForExit()
		}

		$process.Dispose()
	}
}

function Merge-PublishDirectory {
	param(
		[Parameter(Mandatory = $true)]
		[string] $SourceRoot,

		[Parameter(Mandatory = $true)]
		[string] $DestinationRoot
	)

	foreach ($sourceDirectory in @(
		Get-ChildItem -LiteralPath $SourceRoot -Recurse -Directory -Force)) {
		$relativePath = [System.IO.Path]::GetRelativePath(
			$SourceRoot,
			$sourceDirectory.FullName)
		$destinationPath = Join-Path $DestinationRoot $relativePath

		if (Test-Path -LiteralPath $destinationPath) {
			if (!(Test-Path -LiteralPath $destinationPath -PathType Container)) {
				throw (
					"Shared-runtime path collision is not a directory: " +
					"$relativePath")
			}

			continue
		}

		New-Item -ItemType Directory -Path $destinationPath | Out-Null
	}

	foreach ($sourceFile in @(
		Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -Force)) {
		$relativePath = [System.IO.Path]::GetRelativePath(
			$SourceRoot,
			$sourceFile.FullName)
		$destinationPath = Join-Path $DestinationRoot $relativePath
		$destinationDirectory = Split-Path -Parent $destinationPath

		if (!(Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
			New-Item -ItemType Directory -Path $destinationDirectory |
				Out-Null
		}

		if (Test-Path -LiteralPath $destinationPath) {
			if (!(Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
				throw (
					"Shared-runtime path collision is not a file: " +
					"$relativePath")
			}

			$sourceHash = (Get-FileHash `
				-LiteralPath $sourceFile.FullName `
				-Algorithm SHA256).Hash
			$destinationHash = (Get-FileHash `
				-LiteralPath $destinationPath `
				-Algorithm SHA256).Hash

			if (!([string]::Equals(
				$sourceHash,
				$destinationHash,
				[StringComparison]::OrdinalIgnoreCase))) {
				throw (
					"Shared-runtime file collision differs by SHA-256: " +
					"$relativePath")
			}

			continue
		}

		Copy-Item `
			-LiteralPath $sourceFile.FullName `
			-Destination $destinationPath
	}
}

function Remove-OwnedPath {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path,

		[switch] $Recurse
	)

	if (!(Test-Path -LiteralPath $Path)) {
		return
	}

	$fullPath = [System.IO.Path]::GetFullPath($Path)

	if ([string]::Equals(
			$fullPath,
			$resolvedOutputRoot,
			[StringComparison]::OrdinalIgnoreCase) -or
		!$fullPath.StartsWith(
			$resolvedOutputRootPrefix,
			[StringComparison]::OrdinalIgnoreCase)) {
		throw "Refusing to clean a path outside the owned output tree: $fullPath"
	}

	$item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop

	if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
		throw "Refusing to clean a reparse point: $fullPath"
	}

	if ($Recurse) {
		if (!$item.PSIsContainer) {
			throw "Recursive cleanup target is not a directory: $fullPath"
		}

		$reparsePoints = @(Get-ChildItem `
			-LiteralPath $fullPath `
			-Force `
			-Recurse `
			-ErrorAction Stop | Where-Object {
				($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
			})

		if ($reparsePoints.Count -ne 0) {
			throw "Refusing to clean a tree containing reparse points: $fullPath"
		}

		Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
	}
	else {
		if ($item.PSIsContainer) {
			throw "Non-recursive cleanup target is not a file: $fullPath"
		}

		Remove-Item -LiteralPath $fullPath -Force -ErrorAction Stop
	}

	if (Test-Path -LiteralPath $fullPath) {
		throw "Cleanup did not remove owned path: $fullPath"
	}
}

try {
	if ((Test-Path -LiteralPath $packageRoot) -or
		(Test-Path -LiteralPath $zipPath) -or
		(Test-Path -LiteralPath $checksumPath)) {
		throw "Version $Version already exists under $resolvedOutputRoot."
	}

	New-Item -ItemType Directory -Path $workingRoot | Out-Null
	New-Item -ItemType Directory -Path $stagedPackageRoot | Out-Null

	Push-Location -LiteralPath $repositoryRoot
	$locationPushed = $true

	$selectedSdkVersionOutput = @()
	& dotnet --version |
		Tee-Object -Variable selectedSdkVersionOutput |
		Out-Null
	$dotnetVersionExitCode = $global:LASTEXITCODE
	$dotnetVersionSucceeded = ($dotnetVersionExitCode -eq 0)
	$selectedSdkVersion = if (@($selectedSdkVersionOutput).Count -gt 0) {
		[string] @($selectedSdkVersionOutput)[0]
	}
	else {
		''
	}
	if (!$dotnetVersionSucceeded -or
		($dotnetVersionExitCode -ne 0) -or
		!([string]::Equals(
			$selectedSdkVersion,
			$requiredSdkVersion,
			[StringComparison]::Ordinal))) {
		throw (
			"Repository global.json requires SDK $requiredSdkVersion, " +
			"but dotnet selected '$selectedSdkVersion'.")
	}

	$sourceRevisionOutput = @()
	& git rev-parse HEAD |
		Tee-Object -Variable sourceRevisionOutput |
		Out-Null
	$gitRevisionExitCode = $global:LASTEXITCODE
	$gitRevisionSucceeded = ($gitRevisionExitCode -eq 0)
	$sourceRevisionId = if (@($sourceRevisionOutput).Count -gt 0) {
		[string] @($sourceRevisionOutput)[0]
	}
	else {
		''
	}
	if (!$gitRevisionSucceeded -or
		($gitRevisionExitCode -ne 0) -or
		!($sourceRevisionId -match '^[0-9a-fA-F]{40}$')) {
		throw "Unable to resolve a full Git HEAD commit for packaging."
	}

	$workingTreeStatus = @()
	& git status --porcelain=v1 --untracked-files=all |
		Tee-Object -Variable workingTreeStatus |
		Out-Null
	$gitStatusExitCode = $global:LASTEXITCODE
	$gitStatusSucceeded = ($gitStatusExitCode -eq 0)
	$workingTreeStatus = @($workingTreeStatus)
	if (!$gitStatusSucceeded -or ($gitStatusExitCode -ne 0)) {
		throw "Unable to verify the Git working tree before packaging."
	}

	if ($workingTreeStatus.Count -gt 0) {
		if (!$AllowDirtyTreeForVerification) {
			throw (
				"Formal packages require a clean Git working tree. " +
				"Commit the reviewed source before publishing.")
		}

		if (!$Version.Contains(
				'verify',
				[StringComparison]::OrdinalIgnoreCase) -or
			[string]::Equals(
				$resolvedOutputRoot,
				$defaultOutputRoot,
				[StringComparison]::OrdinalIgnoreCase) -or
			$resolvedOutputRoot.StartsWith(
				$defaultOutputRootPrefix,
				[StringComparison]::OrdinalIgnoreCase)) {
			throw (
				"Dirty-tree verification requires a version containing " +
				"'verify' and an OutputRoot outside repository publish.")
		}

		$sourceRevisionId = "$sourceRevisionId-dirty"
	}

	Invoke-PinnedPublish `
		-ProjectPath (Join-Path `
			$repositoryRoot `
			'src\AiUsageDashboard.App\AiUsageDashboard.App.csproj') `
		-PublishRoot $appRoot `
		-DisplayName 'Dashboard'
	Assert-PublishedProductVersion `
		-ExecutablePath (Join-Path $appRoot 'AiUsageDashboard.App.exe')
	Assert-PublishedRuntime `
		-PublishRoot $appRoot `
		-AssemblyName 'AiUsageDashboard.App'

	Invoke-PinnedPublish `
		-ProjectPath (Join-Path `
			$repositoryRoot `
			'src\AiUsageDashboard.Antigravity.Setup\AiUsageDashboard.Antigravity.Setup.csproj') `
		-PublishRoot $setupPublishRoot `
		-DisplayName 'Antigravity setup'
	Assert-PublishedProductVersion `
		-ExecutablePath (Join-Path $setupPublishRoot 'AiUsageDashboard.Antigravity.Setup.exe')
	Assert-PublishedRuntime `
		-PublishRoot $setupPublishRoot `
		-AssemblyName 'AiUsageDashboard.Antigravity.Setup'

	Merge-PublishDirectory `
		-SourceRoot $setupPublishRoot `
		-DestinationRoot $appRoot
	Remove-Item -LiteralPath $setupPublishRoot -Recurse -Force

	Invoke-PinnedTrimmedSingleFilePublish `
		-ProjectPath (Join-Path `
			$repositoryRoot `
			'src\AiUsageDashboard.AntigravityCapture\AiUsageDashboard.AntigravityCapture.csproj') `
		-PublishRoot $antigravityCapturePublishRoot `
		-DisplayName 'Antigravity status-line capture' `
		-ExecutableName 'AiUsageDashboard.AntigravityCapture.exe'
	Assert-PublishedProductVersion `
		-ExecutablePath (Join-Path `
			$antigravityCapturePublishRoot `
			'AiUsageDashboard.AntigravityCapture.exe')
	Invoke-PublishedCaptureSmokeTest `
		-ExecutablePath (Join-Path `
			$antigravityCapturePublishRoot `
			'AiUsageDashboard.AntigravityCapture.exe')
	Merge-PublishDirectory `
		-SourceRoot $antigravityCapturePublishRoot `
		-DestinationRoot $appRoot
	Remove-Item `
		-LiteralPath $antigravityCapturePublishRoot `
		-Recurse `
		-Force

	$rootGuide = [IO.File]::ReadAllText(
		(Join-Path $repositoryRoot '使用說明.md')).Replace(
			'](third-party-notices/',
			'](app/third-party-notices/')
	[IO.File]::WriteAllText(
		(Join-Path $stagedPackageRoot '使用說明.md'),
		$rootGuide,
		[Text.UTF8Encoding]::new($false))

	$forbiddenNames = @(
		'AiUsageDashboard.AntigravitySpike.exe',
		'AiUsageDashboard.AntigravitySpike.dll',
		'AiUsageDashboard.AntigravitySpike.deps.json',
		'AiUsageDashboard.AntigravitySpike.runtimeconfig.json',
		'.env',
		'accounts.json',
		'accounts.json.bak',
		'appsettings.Local.json',
		'auth.json',
		'credentials.json',
		'INTERNAL_DISTRIBUTION.md',
		'preferences.json',
		'secrets.json'
	)
	$forbiddenPatterns = @(
		'.env.*',
		'accounts.corrupt-*.json',
		'auth.*.json',
		'credentials.*.json',
		'*.pdb',
		'*.jwk',
		'*.key',
		'*.p12',
		'*.pem',
		'*.pfx',
		'*.pk8',
		'private-key*',
		'agy-profile*.json',
		'agy-*.profile.json',
		'antigravity-profile*.json',
		'*.private-profile.json',
		'*.profile.private.json',
		'diagnostics.log',
		'usage-snapshot*.json'
	)
	$forbidden = @(Get-ChildItem `
		-LiteralPath $stagedPackageRoot `
		-Recurse `
		-File |
		Where-Object {
			$fileName = $_.Name
			($forbiddenNames -contains $fileName) -or
			(@($forbiddenPatterns | Where-Object {
				$fileName -like $_
			}).Count -gt 0)
		})

	if ($forbidden.Count -gt 0) {
		$relative = $forbidden |
			ForEach-Object {
				[System.IO.Path]::GetRelativePath(
					$stagedPackageRoot,
					$_.FullName)
			}
		throw "Forbidden package content detected: $($relative -join ', ')"
	}

	$legalManifestPath = Join-Path $repositoryRoot 'third-party-notices/component-manifest.json'
	$legalManifest = Get-Content -LiteralPath $legalManifestPath -Raw | ConvertFrom-Json
	& (Join-Path $PSScriptRoot 'Assert-LicenseDependencies.ps1') `
		-DepsPath (Join-Path $appRoot 'AiUsageDashboard.App.deps.json') `
		-Profile app `
		-ExpectedManifestPath $legalManifestPath `
		-SelfContained
	& (Join-Path $PSScriptRoot 'Assert-LicenseDependencies.ps1') `
		-DepsPath (Join-Path $appRoot 'AiUsageDashboard.Antigravity.Setup.deps.json') `
		-Profile setup `
		-ExpectedManifestPath $legalManifestPath `
		-SelfContained
	& (Join-Path $PSScriptRoot 'Assert-LicenseDependencies.ps1') `
		-DepsPath (Join-Path $appRoot 'AiUsageDashboard.ClaudeCapture.deps.json') `
		-Profile claude-capture `
		-ExpectedManifestPath $legalManifestPath `
		-SelfContained
	foreach ($document in $legalManifest.documents) {
		$noticePath = [IO.Path]::GetFullPath((Join-Path $appRoot $document.path))
		$appPrefix = [IO.Path]::GetFullPath($appRoot) + [IO.Path]::DirectorySeparatorChar
		if (!$noticePath.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase) -or
			!(Test-Path -LiteralPath $noticePath -PathType Leaf) -or
			((Get-FileHash -LiteralPath $noticePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $document.sha256)) {
			throw "Required license document is missing or differs: $($document.path)"
		}
	}
	if ((Get-FileHash -LiteralPath $legalManifestPath -Algorithm SHA256).Hash -cne
		(Get-FileHash -LiteralPath (Join-Path $appRoot 'third-party-notices/component-manifest.json') -Algorithm SHA256).Hash) {
		throw 'The packaged component manifest differs from the source manifest.'
	}
	foreach ($entry in @(
		@('AiUsageDashboard.App.exe', 'app'),
		@('AiUsageDashboard.Antigravity.Setup.exe', 'setup'),
		@('AiUsageDashboard.AntigravityCapture.exe', 'capture'),
		@('AiUsageDashboard.ClaudeCapture.exe', 'claude-capture')
	)) {
		& (Join-Path $PSScriptRoot 'Assert-LicensePayload.ps1') `
			-ExecutablePath (Join-Path $appRoot $entry[0]) `
			-Profile $entry[1] `
			-ExpectedManifestPath $legalManifestPath `
			-WorkingRoot $workingRoot
	}

	$requiredFiles = @(
		(Join-Path $appRoot 'LICENSE'),
		(Join-Path $appRoot 'AiUsageDashboard.Licensing.dll'),
		(Join-Path $appRoot 'third-party-notices/component-manifest.json'),
		(Join-Path $appRoot 'AiUsageDashboard.App.exe'),
		(Join-Path $appRoot 'AiUsageDashboard.App.runtimeconfig.json'),
		(Join-Path $appRoot 'AiUsageDashboard.App.deps.json'),
		(Join-Path $appRoot 'AiUsageDashboard.Antigravity.Setup.exe'),
		(Join-Path `
			$appRoot `
			'AiUsageDashboard.Antigravity.Setup.runtimeconfig.json'),
		(Join-Path $appRoot 'AiUsageDashboard.Antigravity.Setup.deps.json'),
		(Join-Path $appRoot 'AiUsageDashboard.AntigravityCapture.exe'),
		(Join-Path $appRoot 'AiUsageDashboard.Antigravity.dll'),
		(Join-Path $appRoot 'README.md'),
		(Join-Path `
			$appRoot `
			'third-party-notices\GitHub-Copilot-SDK-LICENSE.md'),
		(Join-Path $stagedPackageRoot '使用說明.md')
	)

	foreach ($requiredFile in $requiredFiles) {
		if (!(Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
			throw "Required package file is missing: $requiredFile"
		}
	}

	Assert-PublishedRuntime `
		-PublishRoot $appRoot `
		-AssemblyName 'AiUsageDashboard.App'
	Assert-PublishedRuntime `
		-PublishRoot $appRoot `
		-AssemblyName 'AiUsageDashboard.Antigravity.Setup'

	Compress-Archive `
		-LiteralPath $stagedPackageRoot `
		-DestinationPath $temporaryZipPath
	$checksum = (Get-FileHash `
		-LiteralPath $temporaryZipPath `
		-Algorithm SHA256).Hash.ToLowerInvariant()
	"$checksum  $([System.IO.Path]::GetFileName($zipPath))" |
		Set-Content `
			-LiteralPath $temporaryChecksumPath `
			-Encoding ascii `
			-NoNewline

	$validatorAssembly = [Reflection.Assembly]::Load(
		[IO.File]::ReadAllBytes(
			(Join-Path $appRoot 'AiUsageDashboard.Updater.Core.dll')))
	$manifestType = $validatorAssembly.GetType(
		'AiUsageDashboard.Updater.Core.UpdateManifest', $true)
	$transactionType = $validatorAssembly.GetType(
		'AiUsageDashboard.Updater.Core.UpdateTransaction', $true)
	$stagerType = $validatorAssembly.GetType(
		'AiUsageDashboard.Updater.Core.UpdatePackageStager', $true)
	$validationManifest = $manifestType::Parse((@{
		schemaVersion = 1
		packageId = 'AiUsageDashboard'
		version = $Version
		runtimeIdentifier = $runtimeIdentifier
		archiveSizeBytes = (Get-Item -LiteralPath $temporaryZipPath).Length
		archiveSha256 = $checksum
	} | ConvertTo-Json))
	$validationTransaction = $transactionType::Create(
		(Join-Path $workingRoot 'package-validation'),
		[Guid]::NewGuid().ToString('N'))
	$stager = [Activator]::CreateInstance($stagerType)
	$null = $stager.StageAsync(
		$temporaryZipPath,
		$validationManifest,
		$validationTransaction,
		[Threading.CancellationToken]::None).GetAwaiter().GetResult()

	[System.IO.Directory]::Move($stagedPackageRoot, $packageRoot)
	$packageRootFinalized = $true
	[System.IO.File]::Move($temporaryZipPath, $zipPath)
	$zipFinalized = $true
	[System.IO.File]::Move(
		$temporaryChecksumPath,
		$checksumPath)
	$checksumFinalized = $true
	Remove-OwnedPath -Path $workingRoot -Recurse

	Write-Output "SDK: $selectedSdkVersion"
	Write-Output "Runtime: $selfContainedRuntimeVersion"
	Write-Output "Package: $zipPath"
	Write-Output "SHA256: $checksum"
	Write-Output "Start: $(Join-Path $packageRoot 'app\AiUsageDashboard.App.exe')"
}
catch {
	$publishFailure = $_
	$cleanupFailures = [System.Collections.Generic.List[string]]::new()

	foreach ($ownedOutput in @(
		@($checksumPath, $checksumFinalized, $false),
		@($zipPath, $zipFinalized, $false),
		@($packageRoot, $packageRootFinalized, $true),
		@($workingRoot, $true, $true))) {
		if (!([bool] $ownedOutput[1])) {
			continue
		}

		try {
			Remove-OwnedPath `
				-Path ([string] $ownedOutput[0]) `
				-Recurse:([bool] $ownedOutput[2])
		}
		catch {
			$cleanupFailures.Add([string] $ownedOutput[0])
		}
	}

	if ($cleanupFailures.Count -gt 0) {
		throw (
			"Internal package failed and cleanup could not remove: " +
			"$($cleanupFailures -join ', '). Original error: " +
			"$($publishFailure.Exception.Message)")
	}

	throw $publishFailure
}
finally {
	if ($locationPushed) {
		Pop-Location
	}
}
