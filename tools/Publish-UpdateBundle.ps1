[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Version,

	[Parameter(Mandatory = $true)]
	[ValidateRange(1, [long]::MaxValue)]
	[long] $ReleaseSequence,

	[Parameter(Mandatory = $true)]
	[ValidateRange(0, [long]::MaxValue)]
	[long] $PreviousReleaseSequence,

	[Parameter(Mandatory = $true)]
	[string] $TrustedKeysFile,

	[Parameter(Mandatory = $true)]
	[string] $SigningKeyId,

	[Parameter(Mandatory = $true)]
	[string] $SigningPrivateKeyFile,

	[ValidatePattern('^[a-z][a-z0-9-]{0,31}$')]
	[string] $Channel = 'stable',

	[string] $MinimumUpdaterVersion,

	[string] $FeedUrl,

	[string] $AssetBaseUrl,

	[string] $OutputRoot,

	[switch] $AllowDirtyTreeForVerification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runtimeIdentifier = 'win-x64'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($ReleaseSequence -le $PreviousReleaseSequence) {
	throw 'ReleaseSequence must exceed the audited previous distribution sequence upper bound.'
}
$resolvedTrustedKeysFile = (Resolve-Path -LiteralPath $TrustedKeysFile -ErrorAction Stop).Path
$resolvedPrivateKeyFile = (Resolve-Path -LiteralPath $SigningPrivateKeyFile -ErrorAction Stop).Path
$signingProject = Join-Path $PSScriptRoot 'AiUsageDashboard.FeedSigning/AiUsageDashboard.FeedSigning.csproj'
$MinimumUpdaterVersion = if ([string]::IsNullOrWhiteSpace(
		$MinimumUpdaterVersion)) {
	$Version
}
else {
	$MinimumUpdaterVersion
}

if ($Version.Contains('+', [StringComparison]::Ordinal) -or
	$MinimumUpdaterVersion.Contains('+', [StringComparison]::Ordinal)) {
	throw 'Version and MinimumUpdaterVersion must not contain build metadata.'
}

try {
	$publishedVersion =
		[Management.Automation.SemanticVersion]::Parse($Version)
	$minimumVersion =
		[Management.Automation.SemanticVersion]::Parse(
			$MinimumUpdaterVersion)
}
catch {
	throw (
		'Version and MinimumUpdaterVersion must be valid SemVer ' +
		'without build metadata.')
}

if ($minimumVersion.CompareTo($publishedVersion) -gt 0) {
	throw 'MinimumUpdaterVersion cannot exceed Version.'
}
if ($MinimumUpdaterVersion -cne $Version) {
	throw 'MinimumUpdaterVersion must equal Version so the current installer license catalog is used.'
}

$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
	Join-Path $repositoryRoot 'publish'
}
else {
	$OutputRoot
}
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$outputRootPrefix =
	$resolvedOutputRoot.TrimEnd(
		[IO.Path]::DirectorySeparatorChar,
		[IO.Path]::AltDirectorySeparatorChar) +
	[IO.Path]::DirectorySeparatorChar
$FeedUrl = if ([string]::IsNullOrWhiteSpace($FeedUrl)) {
	"https://github.com/Sokaka/ai-usage-dashboard/releases/latest/download/AiUsageDashboard-update-$Channel.json"
}
else {
	$FeedUrl
}
$AssetBaseUrl = if ([string]::IsNullOrWhiteSpace($AssetBaseUrl)) {
	"https://github.com/Sokaka/ai-usage-dashboard/releases/download/v$Version"
}
else {
	$AssetBaseUrl.TrimEnd('/')
}
$feedUri = [Uri] $FeedUrl
$assetBaseUri = [Uri] $AssetBaseUrl

foreach ($uriContract in @(
	@('FeedUrl', $feedUri),
	@('AssetBaseUrl', $assetBaseUri))) {
	$name = [string] $uriContract[0]
	$uri = [Uri] $uriContract[1]

	if (!$uri.IsAbsoluteUri -or
		!([string]::Equals(
			$uri.Scheme,
			[Uri]::UriSchemeHttps,
			[StringComparison]::OrdinalIgnoreCase)) -or
		!([string]::IsNullOrEmpty($uri.UserInfo)) -or
		!([string]::IsNullOrEmpty($uri.Fragment))) {
		throw "$name must be an absolute HTTPS URL without credentials or a fragment."
	}
}

$packageName = "AiUsageDashboard-$Version-$runtimeIdentifier.zip"
$updaterName = "AiUsageDashboard-Updater-$Version-$runtimeIdentifier.exe"
$feedName = "AiUsageDashboard-update-$Channel.json"
$artifactNames = @(
	$packageName,
	"$packageName.sha256",
	$updaterName,
	"$updaterName.sha256",
	$feedName,
	"$feedName.sha256"
)
$workingRoot = Join-Path `
	$resolvedOutputRoot `
	".aud-update-bundle-$([Guid]::NewGuid().ToString('N'))"
$bundleRoot = Join-Path $workingRoot 'bundle'
$finalizedPaths = [Collections.Generic.List[string]]::new()

function Remove-OwnedWorkingRoot {
	if (!(Test-Path -LiteralPath $workingRoot)) {
		return
	}

	$resolvedWorkingRoot = [IO.Path]::GetFullPath($workingRoot)
	if (!$resolvedWorkingRoot.StartsWith(
		$outputRootPrefix,
		[StringComparison]::OrdinalIgnoreCase) -or
		!(Split-Path -Leaf $resolvedWorkingRoot).StartsWith(
			'.aud-update-bundle-',
			[StringComparison]::Ordinal)) {
		throw 'Refusing to clean an update bundle path outside the owned output tree.'
	}

	$reparsePoints = @(
		@(
			Get-Item -LiteralPath $resolvedWorkingRoot -Force
			Get-ChildItem `
				-LiteralPath $resolvedWorkingRoot `
				-Recurse `
				-Force
		) | Where-Object {
			($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
		})
	if ($reparsePoints.Count -ne 0) {
		throw 'Refusing to clean an update bundle tree containing reparse points.'
	}

	$lastCleanupError = $null
	for ($attempt = 1; $attempt -le 5; $attempt++) {
		try {
			Remove-Item `
				-LiteralPath $resolvedWorkingRoot `
				-Recurse `
				-Force `
				-ErrorAction Stop
			$lastCleanupError = $null
			break
		}
		catch {
			$lastCleanupError = $_

			if ($attempt -lt 5) {
				Start-Sleep -Milliseconds 200
			}
		}
	}

	if ($null -ne $lastCleanupError) {
		throw $lastCleanupError
	}

	if (Test-Path -LiteralPath $resolvedWorkingRoot) {
		throw "Cleanup did not remove owned update bundle path: $resolvedWorkingRoot"
	}
}

function Read-CanonicalChecksum {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ArtifactPath
	)

	$sidecarPath = "$ArtifactPath.sha256"
	$sidecar = (Get-Content -LiteralPath $sidecarPath -Raw).Trim()
	$escapedFileName = [Regex]::Escape((Split-Path -Leaf $ArtifactPath))

	if ($sidecar -cnotmatch "^(?<hash>[0-9a-f]{64})  $escapedFileName$") {
		throw "Checksum sidecar is not canonical: $sidecarPath"
	}

	$actualHash = (Get-FileHash `
		-LiteralPath $ArtifactPath `
		-Algorithm SHA256).Hash.ToLowerInvariant()
	if (![string]::Equals(
		$Matches['hash'],
		$actualHash,
		[StringComparison]::Ordinal)) {
		throw "Checksum sidecar does not match: $sidecarPath"
	}

	return $actualHash
}

try {
	New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null

	foreach ($artifactName in $artifactNames) {
		$finalPath = Join-Path $resolvedOutputRoot $artifactName
		if (Test-Path -LiteralPath $finalPath) {
			throw "Update bundle artifact already exists: $finalPath"
		}
	}

	New-Item -ItemType Directory -Path $bundleRoot | Out-Null
	$publishArguments = @{
		Version = $Version
		OutputRoot = $bundleRoot
	}
	if ($AllowDirtyTreeForVerification) {
		$publishArguments['AllowDirtyTreeForVerification'] = $true
	}

	& (Join-Path $PSScriptRoot 'Publish-Internal.ps1') @publishArguments
	& (Join-Path $PSScriptRoot 'Publish-Updater.ps1') `
		@publishArguments `
		-FeedUrl $FeedUrl `
		-Channel $Channel `
		-TrustedKeysFile $resolvedTrustedKeysFile

	$packagePath = Join-Path $bundleRoot $packageName
	$updaterPath = Join-Path $bundleRoot $updaterName
	$packageHash = Read-CanonicalChecksum -ArtifactPath $packagePath
	$updaterHash = Read-CanonicalChecksum -ArtifactPath $updaterPath
	$sourceRevisionOutput = @(& git -C $repositoryRoot rev-parse HEAD)
	if (($LASTEXITCODE -ne 0) -or
		($sourceRevisionOutput.Count -ne 1) -or
		($sourceRevisionOutput[0] -notmatch '^[0-9a-fA-F]{40}$')) {
		throw 'Unable to resolve the source revision for update metadata.'
	}

	$sourceRevision = ([string] $sourceRevisionOutput[0]).ToLowerInvariant()
	$workingTreeStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
	if ($LASTEXITCODE -ne 0) {
		throw 'Unable to verify the working tree for update metadata.'
	}
	if ($workingTreeStatus.Count -ne 0) {
		if (!$AllowDirtyTreeForVerification) {
			throw 'Formal update metadata requires a clean working tree.'
		}

		$sourceRevision = "$sourceRevision-dirty"
	}

	$packageInfo = Get-Item -LiteralPath $packagePath
	$updaterInfo = Get-Item -LiteralPath $updaterPath
	$feed = [ordered] @{
		schemaVersion = 1
		channel = $Channel
		releaseSequence = $ReleaseSequence
		minimumUpdaterVersion = $MinimumUpdaterVersion
		package = [ordered] @{
			artifactId = 'AiUsageDashboard'
			version = $Version
			runtimeIdentifier = $runtimeIdentifier
			fileName = $packageName
			downloadUrl = "$AssetBaseUrl/$packageName"
			sizeBytes = $packageInfo.Length
			sha256 = $packageHash
			sourceRevision = $sourceRevision
		}
		updater = [ordered] @{
			artifactId = 'AiUsageDashboard.Updater'
			version = $Version
			runtimeIdentifier = $runtimeIdentifier
			fileName = $updaterName
			downloadUrl = "$AssetBaseUrl/$updaterName"
			sizeBytes = $updaterInfo.Length
			sha256 = $updaterHash
			sourceRevision = $sourceRevision
		}
	}
	$feedPath = Join-Path $bundleRoot $feedName
	$payloadPath = Join-Path $workingRoot 'unsigned-payload.json'
	$verifiedPayloadPath = Join-Path $workingRoot 'verified-payload.json'
	[IO.File]::WriteAllText(
		$payloadPath,
		($feed | ConvertTo-Json -Depth 4),
		[Text.UTF8Encoding]::new($false))
	& dotnet run --project $signingProject --configuration Release -- sign `
		--payload $payloadPath --trust $resolvedTrustedKeysFile `
		--key-id $SigningKeyId --private-key-file $resolvedPrivateKeyFile `
		--output $feedPath --channel $Channel
	if ($LASTEXITCODE -ne 0) {
		throw 'Unable to sign the completed update bundle feed.'
	}
	& dotnet run --project $signingProject --configuration Release --no-build -- verify `
		--feed $feedPath --trust $resolvedTrustedKeysFile `
		--channel $Channel --output $verifiedPayloadPath
	if ($LASTEXITCODE -ne 0) {
		throw 'Generated signed update feed failed runtime verification.'
	}

	$feedHash = (Get-FileHash `
		-LiteralPath $feedPath `
		-Algorithm SHA256).Hash.ToLowerInvariant()
	"$feedHash  $feedName" |
		Set-Content `
			-LiteralPath "$feedPath.sha256" `
			-Encoding ascii `
			-NoNewline

	foreach ($artifactName in $artifactNames) {
		$sourcePath = Join-Path $bundleRoot $artifactName
		$destinationPath = Join-Path $resolvedOutputRoot $artifactName
		[IO.File]::Move($sourcePath, $destinationPath)
		$finalizedPaths.Add($destinationPath)
	}

	Remove-OwnedWorkingRoot
	Write-Output "Package: $(Join-Path $resolvedOutputRoot $packageName)"
	Write-Output "Updater: $(Join-Path $resolvedOutputRoot $updaterName)"
	Write-Output "Feed: $(Join-Path $resolvedOutputRoot $feedName)"
	Write-Output "Channel: $Channel"
	Write-Output "ReleaseSequence: $ReleaseSequence"
}
catch {
	$originalError = $_
	$cleanupFailures = [Collections.Generic.List[string]]::new()

	foreach ($finalizedPath in $finalizedPaths) {
		try {
			if (Test-Path -LiteralPath $finalizedPath -PathType Leaf) {
				Remove-Item -LiteralPath $finalizedPath -Force
			}
		}
		catch {
			$cleanupFailures.Add($finalizedPath)
		}
	}

	try {
		Remove-OwnedWorkingRoot
	}
	catch {
		$cleanupFailures.Add(
			('{0}: {1}' -f $workingRoot, $_.Exception.Message))
	}

	if ($cleanupFailures.Count -ne 0) {
		throw [AggregateException]::new(
			'Update bundle failed and cleanup was incomplete.',
			@($originalError.Exception) +
				@($cleanupFailures | ForEach-Object {
					[IO.IOException]::new("Cleanup failed: $_")
				}))
	}

	throw $originalError
}
