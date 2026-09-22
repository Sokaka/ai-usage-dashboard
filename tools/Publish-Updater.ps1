#Requires -Version 7.4
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Version,

	[string] $OutputRoot,

	[Parameter(Mandatory = $true)]
	[string] $TrustedKeysFile,

	[string] $FeedUrl =
		'https://github.com/Sokaka/ai-usage-dashboard/releases/latest/download/AiUsageDashboard-update-stable.json',

	[ValidatePattern('^[a-z][a-z0-9-]{0,31}$')]
	[string] $Channel = 'stable',

	[switch] $AllowDirtyTreeForVerification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredSdkVersion = '8.0.425'
$selfContainedRuntimeVersion = '8.0.31'
$runtimeIdentifier = 'win-x64'
$parsedFeedUri = $null

if ($Version.Contains('+', [StringComparison]::Ordinal)) {
	throw "Version must not contain SemVer build metadata: $Version"
}

try {
	$null = [Management.Automation.SemanticVersion]::Parse($Version)
}
catch {
	throw "Version must be valid SemVer without build metadata: $Version"
}

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
	!([string]::IsNullOrEmpty($parsedFeedUri.UserInfo)) -or
	!([string]::IsNullOrEmpty($parsedFeedUri.Fragment))) {
	throw 'FeedUrl must be an absolute HTTPS URL without credentials or a fragment.'
}
$repositoryRoot = [System.IO.Path]::GetFullPath(
	(Join-Path $PSScriptRoot '..'))
$resolvedTrustedKeysFile = (Resolve-Path -LiteralPath $TrustedKeysFile -ErrorAction Stop).Path
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
$artifactName = "AiUsageDashboard-Updater-$Version-$runtimeIdentifier.exe"
$artifactPath = Join-Path $resolvedOutputRoot $artifactName
$checksumPath = "$artifactPath.sha256"
$workingRoot = Join-Path `
	-Path $resolvedOutputRoot `
	-ChildPath ".aud-updater-build-$([Guid]::NewGuid().ToString('N'))"
$publishRoot = Join-Path $workingRoot 'publish'
$temporaryArtifactPath = Join-Path $workingRoot $artifactName
$temporaryChecksumPath = "$temporaryArtifactPath.sha256"
$artifactFinalized = $false
$checksumFinalized = $false
$locationPushed = $false

function Remove-OwnedWorkingRoot {
	if (!(Test-Path -LiteralPath $workingRoot)) {
		return
	}

	$resolvedWorkingRoot = [System.IO.Path]::GetFullPath($workingRoot)
	if (!$resolvedWorkingRoot.StartsWith(
			$resolvedOutputRootPrefix,
			[StringComparison]::OrdinalIgnoreCase) -or
		!(Split-Path -Leaf $resolvedWorkingRoot).StartsWith(
			'.aud-updater-build-',
			[StringComparison]::Ordinal)) {
		throw "Refusing to clean an updater path outside the owned output tree."
	}

	$ownedEntries = @(
		Get-Item -LiteralPath $resolvedWorkingRoot -Force
		Get-ChildItem `
			-LiteralPath $resolvedWorkingRoot `
			-Recurse `
			-Force
	)
	foreach ($entry in $ownedEntries) {
		if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
			throw (
				"Refusing to clean an updater tree containing reparse points: " +
				$entry.FullName)
		}
	}

	Remove-Item -LiteralPath $resolvedWorkingRoot -Recurse -Force
	if (Test-Path -LiteralPath $resolvedWorkingRoot) {
		throw "Cleanup did not remove owned updater path: $resolvedWorkingRoot"
	}
}

function Remove-OwnedFinalizedFile {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path
	)

	$resolvedPath = [System.IO.Path]::GetFullPath($Path)
	if (!$resolvedPath.StartsWith(
			$resolvedOutputRootPrefix,
			[StringComparison]::OrdinalIgnoreCase)) {
		throw "Refusing to remove an updater artifact outside the output root."
	}

	if (Test-Path -LiteralPath $resolvedPath -PathType Leaf) {
		$file = Get-Item -LiteralPath $resolvedPath -Force
		if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
			throw "Refusing to remove a reparse-point updater artifact."
		}

		Remove-Item -LiteralPath $resolvedPath -Force
	}
}

try {
	if ((Test-Path -LiteralPath $artifactPath) -or
		(Test-Path -LiteralPath $checksumPath)) {
		throw "Updater version $Version already exists under $resolvedOutputRoot."
	}

	New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
	New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
	Push-Location -LiteralPath $repositoryRoot
	$locationPushed = $true

	$selectedSdkVersionOutput = @()
	& dotnet --version |
		Tee-Object -Variable selectedSdkVersionOutput |
		Out-Null
	$dotnetVersionExitCode = $global:LASTEXITCODE
	$selectedSdkVersion = if (@($selectedSdkVersionOutput).Count -gt 0) {
		[string] @($selectedSdkVersionOutput)[0]
	}
	else {
		''
	}
	if (($dotnetVersionExitCode -ne 0) -or
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
	$sourceRevisionId = if (@($sourceRevisionOutput).Count -gt 0) {
		[string] @($sourceRevisionOutput)[0]
	}
	else {
		''
	}
	if (($gitRevisionExitCode -ne 0) -or
		!($sourceRevisionId -match '^[0-9a-fA-F]{40}$')) {
		throw "Unable to resolve a full Git HEAD commit for updater packaging."
	}

	$workingTreeStatus = @()
	& git status --porcelain=v1 --untracked-files=all |
		Tee-Object -Variable workingTreeStatus |
		Out-Null
	$gitStatusExitCode = $global:LASTEXITCODE
	$workingTreeStatus = @($workingTreeStatus)
	if ($gitStatusExitCode -ne 0) {
		throw "Unable to verify the Git working tree before updater packaging."
	}

	if ($workingTreeStatus.Count -gt 0) {
		if (!$AllowDirtyTreeForVerification) {
			throw "Formal updater packages require a clean Git working tree."
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

	& dotnet publish `
		(Join-Path `
			$repositoryRoot `
			'src\AiUsageDashboard.Updater\AiUsageDashboard.Updater.csproj') `
		--configuration Release `
		--runtime $runtimeIdentifier `
		--self-contained true `
		--disable-build-servers `
		-m:1 `
		-nodeReuse:false `
		-p:UseSharedCompilation=false `
		-p:RuntimeFrameworkVersion=$selfContainedRuntimeVersion `
		-p:TargetLatestRuntimePatch=false `
		-p:PublishSingleFile=true `
		-p:IncludeNativeLibrariesForSelfExtract=true `
		-p:EnableCompressionInSingleFile=true `
		-p:DebugType=None `
		-p:DebugSymbols=false `
		-p:Version=$Version `
		-p:SourceRevisionId=$sourceRevisionId `
		-p:UpdateFeedUrl=$FeedUrl `
		-p:UpdateChannel=$Channel `
		-p:UpdateTrustedKeysFile=$resolvedTrustedKeysFile `
		--output $publishRoot
	$publishSucceeded = $?
	$publishExitCode = $global:LASTEXITCODE
	if (!$publishSucceeded -or ($publishExitCode -ne 0)) {
		throw "Updater publish failed with exit code $publishExitCode."
	}

	$publishedFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse)
	$publishedExecutable = Join-Path $publishRoot 'AiUsageDashboard.Updater.exe'
	if (($publishedFiles.Count -ne 1) -or
		!([string]::Equals(
			$publishedFiles[0].FullName,
			$publishedExecutable,
			[StringComparison]::OrdinalIgnoreCase))) {
		throw "Updater single-file publish produced unexpected files."
	}

	$expectedProductVersion = "$Version+$sourceRevisionId"
	& (Join-Path $PSScriptRoot 'Assert-LicensePayload.ps1') `
		-ExecutablePath $publishedExecutable `
		-Profile installer `
		-ExpectedManifestPath (Join-Path $repositoryRoot 'third-party-notices/component-manifest.json') `
		-WorkingRoot $workingRoot
	$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
		$publishedExecutable).ProductVersion
	if (!([string]::Equals(
			$productVersion,
			$expectedProductVersion,
			[StringComparison]::Ordinal))) {
		throw (
			"Published updater ProductVersion '$productVersion' does not " +
			"match expected '$expectedProductVersion'.")
	}

	Move-Item `
		-LiteralPath $publishedExecutable `
		-Destination $temporaryArtifactPath
	$checksum = (Get-FileHash `
		-LiteralPath $temporaryArtifactPath `
		-Algorithm SHA256).Hash.ToLowerInvariant()
	"$checksum  $artifactName" |
		Set-Content `
			-LiteralPath $temporaryChecksumPath `
			-Encoding ascii `
			-NoNewline

	[System.IO.File]::Move($temporaryArtifactPath, $artifactPath)
	$artifactFinalized = $true
	[System.IO.File]::Move($temporaryChecksumPath, $checksumPath)
	$checksumFinalized = $true
	Remove-OwnedWorkingRoot

	Write-Output "SDK: $selectedSdkVersion"
	Write-Output "Runtime: $selfContainedRuntimeVersion"
	Write-Output "Updater: $artifactPath"
	Write-Output "SHA256: $checksum"
	Write-Output "Feed: $FeedUrl"
	Write-Output "Channel: $Channel"
}
catch {
	$originalError = $_
	$cleanupErrors = [System.Collections.Generic.List[string]]::new()

	if ($locationPushed) {
		Pop-Location
		$locationPushed = $false
	}

	foreach ($cleanup in @(
		@($workingRoot, $true),
		@($checksumPath, $checksumFinalized),
		@($artifactPath, $artifactFinalized)
	)) {
		try {
			if ($cleanup[1]) {
				if ([string]::Equals(
						$cleanup[0],
						$workingRoot,
						[StringComparison]::OrdinalIgnoreCase)) {
					Remove-OwnedWorkingRoot
				}
				else {
					Remove-OwnedFinalizedFile -Path $cleanup[0]
				}
			}
		}
		catch {
			$cleanupErrors.Add($_.Exception.Message)
		}
	}

	if ($cleanupErrors.Count -ne 0) {
		throw [AggregateException]::new(
			"Updater publish failed and cleanup was incomplete.",
			@($originalError.Exception) +
				@($cleanupErrors | ForEach-Object {
					[InvalidOperationException]::new($_)
				}))
	}

	throw $originalError
}
finally {
	if ($locationPushed) {
		Pop-Location
	}
}
