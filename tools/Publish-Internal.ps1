#Requires -Version 7.4
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
	[string] $Version,

	[Parameter(Mandatory = $true)]
	[string] $FeedUrl,

	[Parameter(Mandatory = $true)]
	[ValidatePattern('^[a-z][a-z0-9-]{0,31}$')]
	[string] $Channel,

	[Parameter(Mandatory = $true)]
	[string] $TrustedKeysFile,

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
$resolvedTrustedKeysFile =
	(Resolve-Path -LiteralPath $TrustedKeysFile -ErrorAction Stop).Path

try {
	$parsedFeedUri = [Uri] $FeedUrl
}
catch {
	throw "FeedUrl is not a valid absolute HTTPS URL: $FeedUrl"
}

if (!$parsedFeedUri.IsAbsoluteUri -or
	!([string]::Equals(
		$parsedFeedUri.Scheme,
		[Uri]::UriSchemeHttps,
		[StringComparison]::OrdinalIgnoreCase)) -or
	[string]::IsNullOrEmpty($parsedFeedUri.Host) -or
	!([string]::IsNullOrEmpty($parsedFeedUri.UserInfo)) -or
	!([string]::IsNullOrEmpty($parsedFeedUri.Fragment))) {
	throw 'FeedUrl must be an absolute HTTPS URL without credentials or a fragment.'
}

& dotnet run --project (Join-Path $PSScriptRoot 'AiUsageDashboard.FeedSigning/AiUsageDashboard.FeedSigning.csproj') `
	--configuration Release -- validate-trust --trust $resolvedTrustedKeysFile
if ($LASTEXITCODE -ne 0) {
	throw 'The externally supplied update feed trust store failed validation.'
}

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

function Assert-PublishedAppUpdateConfiguration {
	param(
		[Parameter(Mandatory = $true)]
		[string] $AssemblyPath,

		[Parameter(Mandatory = $true)]
		[string] $ExpectedFeedUrl,

		[Parameter(Mandatory = $true)]
		[string] $ExpectedChannel,

		[Parameter(Mandatory = $true)]
		[string] $ExpectedTrustedKeysFile
	)

	if (!(Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
		throw "Published App assembly is missing: $AssemblyPath"
	}

	$assembly = [Reflection.Assembly]::Load(
		[IO.File]::ReadAllBytes($AssemblyPath))
	$metadata = @($assembly.GetCustomAttributes(
		[Reflection.AssemblyMetadataAttribute],
		$false))

	foreach ($expectedMetadata in @(
		@('AiUsageDashboard.UpdateFeedUrl', $ExpectedFeedUrl),
		@('AiUsageDashboard.UpdateChannel', $ExpectedChannel))) {
		$metadataName = [string] $expectedMetadata[0]
		$expectedValue = [string] $expectedMetadata[1]
		$matchingMetadata = @($metadata | Where-Object {
			[string]::Equals(
				$_.Key,
				$metadataName,
				[StringComparison]::Ordinal)
		})

		if (($matchingMetadata.Count -ne 1) -or
			!([string]::Equals(
				$matchingMetadata[0].Value,
				$expectedValue,
				[StringComparison]::Ordinal))) {
			throw (
				"Published App metadata '$metadataName' does not exactly " +
				"match the requested value.")
		}
	}

	$resourceName = 'AiUsageDashboard.UpdateTrustedKeys.json'
	$matchingResourceNames = @($assembly.GetManifestResourceNames() |
		Where-Object {
			[string]::Equals(
				$_,
				$resourceName,
				[StringComparison]::Ordinal)
		})
	if ($matchingResourceNames.Count -ne 1) {
		throw (
			"Published App must contain exactly one embedded resource " +
			"named '$resourceName'.")
	}

	$resourceStream = $assembly.GetManifestResourceStream($resourceName)
	if ($null -eq $resourceStream) {
		throw "Published App resource '$resourceName' could not be opened."
	}

	$resourceBuffer = [IO.MemoryStream]::new()
	try {
		$resourceStream.CopyTo($resourceBuffer)
		$publishedTrustedKeys = $resourceBuffer.ToArray()
	}
	finally {
		$resourceBuffer.Dispose()
		$resourceStream.Dispose()
	}

	$expectedTrustedKeys = [IO.File]::ReadAllBytes(
		$ExpectedTrustedKeysFile)
	if (!([Collections.StructuralComparisons]::StructuralEqualityComparer.Equals(
			$publishedTrustedKeys,
			$expectedTrustedKeys))) {
		throw (
			"Published App resource '$resourceName' does not byte-match " +
			"the externally supplied trust store.")
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
		-p:UpdateFeedUrl=$FeedUrl `
		-p:UpdateChannel=$Channel `
		-p:UpdateTrustedKeysFile=$resolvedTrustedKeysFile `
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
	Assert-PublishedProductVersion `
		-ExecutablePath (Join-Path $appRoot 'AiUsageDashboard.ClaudeCapture.exe')
	Assert-PublishedRuntime `
		-PublishRoot $appRoot `
		-AssemblyName 'AiUsageDashboard.App'

	$rootGuide = [IO.File]::ReadAllText(
		(Join-Path $repositoryRoot '使用說明.md')).Replace(
			'](third-party-notices/',
			'](app/third-party-notices/')
	[IO.File]::WriteAllText(
		(Join-Path $stagedPackageRoot '使用說明.md'),
		$rootGuide,
		[Text.UTF8Encoding]::new($false))

	$forbiddenNames = @(
		'AiUsageDashboard.Antigravity.Setup.exe',
		'AiUsageDashboard.Antigravity.Setup.deps.json',
		'AiUsageDashboard.Antigravity.Setup.runtimeconfig.json',
		'AiUsageDashboard.AntigravityCapture.exe',
		'AiUsageDashboard.AntigravityCapture.dll',
		'AiUsageDashboard.AntigravityCapture.deps.json',
		'AiUsageDashboard.AntigravityCapture.runtimeconfig.json',
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
		'AiUsageDashboard.AntigravityCapture*.exe',
		'*.private-profile.json',
		'*.profile.private.json',
		'diagnostics.log',
		'usage-snapshot*.json'
	)
	$forbidden = @(Get-ChildItem `
		-LiteralPath $stagedPackageRoot `
		-Recurse `
		-Force `
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

	$allowedExecutablePaths =
		[System.Collections.Generic.HashSet[string]]::new(
			[System.StringComparer]::OrdinalIgnoreCase)
	$null = $allowedExecutablePaths.Add(
		'app\AiUsageDashboard.App.exe')
	$null = $allowedExecutablePaths.Add(
		'app\AiUsageDashboard.ClaudeCapture.exe')
	$unexpectedExecutables = @(Get-ChildItem `
		-LiteralPath $stagedPackageRoot `
		-Recurse `
		-Force `
		-File `
		-Filter '*.exe' |
		Where-Object {
			$relativePath = [System.IO.Path]::GetRelativePath(
				$stagedPackageRoot,
				$_.FullName)
			!$allowedExecutablePaths.Contains($relativePath)
		})

	if ($unexpectedExecutables.Count -gt 0) {
		$relative = $unexpectedExecutables |
			ForEach-Object {
				[System.IO.Path]::GetRelativePath(
					$stagedPackageRoot,
					$_.FullName)
			}
		throw "Unexpected executable detected: $($relative -join ', ')"
	}

	$legalManifestPath = Join-Path $repositoryRoot 'third-party-notices/component-manifest.json'
	$legalManifest = Get-Content -LiteralPath $legalManifestPath -Raw | ConvertFrom-Json
	& (Join-Path $PSScriptRoot 'Assert-LicenseDependencies.ps1') `
		-DepsPath (Join-Path $appRoot 'AiUsageDashboard.App.deps.json') `
		-Profile app `
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
		(Join-Path $appRoot 'AiUsageDashboard.Antigravity.Setup.dll'),
		(Join-Path $appRoot 'AiUsageDashboard.Antigravity.dll'),
		(Join-Path $appRoot 'AiUsageDashboard.ClaudeCapture.exe'),
		(Join-Path $appRoot 'AiUsageDashboard.ClaudeCapture.runtimeconfig.json'),
		(Join-Path $appRoot 'AiUsageDashboard.ClaudeCapture.deps.json'),
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
	Assert-PublishedAppUpdateConfiguration `
		-AssemblyPath (Join-Path $appRoot 'AiUsageDashboard.App.dll') `
		-ExpectedFeedUrl $FeedUrl `
		-ExpectedChannel $Channel `
		-ExpectedTrustedKeysFile $resolvedTrustedKeysFile
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
