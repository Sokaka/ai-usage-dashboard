#Requires -Version 7.4
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

	[string] $PreviousSignedFeedPath,

	[string] $PreviousUpdaterPath,

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
$reusePreviousUpdater = ![string]::IsNullOrWhiteSpace($PreviousSignedFeedPath)
if ($reusePreviousUpdater -ne ![string]::IsNullOrWhiteSpace($PreviousUpdaterPath)) {
	throw 'PreviousSignedFeedPath and PreviousUpdaterPath must be supplied together.'
}
$MinimumUpdaterVersion = if ([string]::IsNullOrWhiteSpace(
		$MinimumUpdaterVersion) -and !$reusePreviousUpdater) {
	$Version
}
else {
	$MinimumUpdaterVersion
}

if ($Version.Contains('+', [StringComparison]::Ordinal) -or
	(![string]::IsNullOrWhiteSpace($MinimumUpdaterVersion) -and
	$MinimumUpdaterVersion.Contains('+', [StringComparison]::Ordinal))) {
	throw 'Version and MinimumUpdaterVersion must not contain build metadata.'
}

$minimumVersion = $null
try {
	$publishedVersion =
		[Management.Automation.SemanticVersion]::Parse($Version)
	if (![string]::IsNullOrWhiteSpace($MinimumUpdaterVersion)) {
		$minimumVersion =
			[Management.Automation.SemanticVersion]::Parse(
				$MinimumUpdaterVersion)
	}
}
catch {
	throw (
		'Version and MinimumUpdaterVersion must be valid SemVer ' +
		'without build metadata.')
}

if (($null -ne $minimumVersion) -and
	($minimumVersion.CompareTo($publishedVersion) -gt 0)) {
	throw 'MinimumUpdaterVersion cannot exceed Version.'
}
if (!$reusePreviousUpdater -and ($MinimumUpdaterVersion -cne $Version)) {
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

function Export-InstallerLicenses {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ExecutablePath,

		[Parameter(Mandatory = $true)]
		[string] $ExportRoot
	)

	$process = [Diagnostics.Process]::new()
	try {
		$process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
		$process.StartInfo.FileName = $ExecutablePath
		$process.StartInfo.UseShellExecute = $false
		$process.StartInfo.CreateNoWindow = $true
		$process.StartInfo.RedirectStandardOutput = $true
		$process.StartInfo.RedirectStandardError = $true
		$process.StartInfo.ArgumentList.Add('--export-licenses')
		$process.StartInfo.ArgumentList.Add($ExportRoot)
		if (!$process.Start()) {
			throw "Unable to start installer license export: $ExecutablePath"
		}

		$stdout = $process.StandardOutput.ReadToEndAsync()
		$stderr = $process.StandardError.ReadToEndAsync()
		if (!$process.WaitForExit(30000)) {
			$process.Kill($true)
			$process.WaitForExit()
			$null = $stdout.GetAwaiter().GetResult()
			$null = $stderr.GetAwaiter().GetResult()
			throw "Installer license export timed out: $ExecutablePath"
		}

		$null = $stdout.GetAwaiter().GetResult()
		$exportError = $stderr.GetAwaiter().GetResult()
		if ($process.ExitCode -ne 0) {
			throw "Installer license export failed for $ExecutablePath (exit $($process.ExitCode)): $exportError"
		}
	}
	finally {
		$process.Dispose()
	}
}

function Assert-InstallerExportsEqual {
	param(
		[Parameter(Mandatory = $true)]
		[string] $PreviousExportRoot,

		[Parameter(Mandatory = $true)]
		[string] $CurrentExportRoot
	)

	$previousEntries = @(Get-ChildItem -LiteralPath $PreviousExportRoot -Recurse -Force)
	$currentEntries = @(Get-ChildItem -LiteralPath $CurrentExportRoot -Recurse -Force)
	foreach ($entry in @($previousEntries) + @($currentEntries)) {
		if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
			throw "Installer license export contains a reparse point: $($entry.FullName)"
		}
	}

	$previousFiles = @($previousEntries | Where-Object { !$_.PSIsContainer })
	$currentFiles = @($currentEntries | Where-Object { !$_.PSIsContainer })
	$previousPaths = @($previousFiles | ForEach-Object {
		[IO.Path]::GetRelativePath($PreviousExportRoot, $_.FullName)
	} | Sort-Object -CaseSensitive)
	$currentPaths = @($currentFiles | ForEach-Object {
		[IO.Path]::GetRelativePath($CurrentExportRoot, $_.FullName)
	} | Sort-Object -CaseSensitive)
	if (($previousPaths.Count -eq 0) -or
		(Compare-Object -CaseSensitive $previousPaths $currentPaths)) {
		throw 'Previous and current installer license exports contain different files.'
	}

	foreach ($relativePath in $previousPaths) {
		$previousFile = Join-Path $PreviousExportRoot $relativePath
		$currentFile = Join-Path $CurrentExportRoot $relativePath
		if (((Get-Item -LiteralPath $previousFile).Length -ne
				(Get-Item -LiteralPath $currentFile).Length) -or
			((Get-FileHash -LiteralPath $previousFile -Algorithm SHA256).Hash -cne
				(Get-FileHash -LiteralPath $currentFile -Algorithm SHA256).Hash)) {
			throw "Installer license export changed: $relativePath"
		}
	}
}

function Read-BundledUpdaterAssembly {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ExecutablePath
	)

	$expectedBundleMajorVersion = 6
	$expectedBundleMinorVersion = 0
	$maxBundleFileCount = 4096
	$maxMainAssemblyBytes = 32MB
	$decompressionBufferBytes = 81920
	$sdkVersion = (Get-Content `
		-LiteralPath (Join-Path $repositoryRoot 'global.json') `
		-Raw | ConvertFrom-Json).sdk.version
	$dotnetExecutable = Get-Command dotnet -CommandType Application |
		Select-Object -First 1
	$dotnetRoot = Split-Path $dotnetExecutable.Source
	$hostModelPath = Join-Path $dotnetRoot "sdk/$sdkVersion/Microsoft.NET.HostModel.dll"
	if (!(Test-Path -LiteralPath $hostModelPath -PathType Leaf)) {
		throw 'The pinned .NET SDK bundle reader dependency is missing.'
	}

	[void] [Reflection.Assembly]::LoadFrom($hostModelPath)
	$headerOffset = [long] 0
	if (![Microsoft.NET.HostModel.AppHost.HostWriter]::IsBundle(
			$ExecutablePath,
			[ref] $headerOffset)) {
		throw 'Previous updater is not a .NET single-file bundle.'
	}

	$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($ExecutablePath))
	try {
		if (($headerOffset -le 0) -or
			($headerOffset -ge $reader.BaseStream.Length)) {
			throw 'Previous updater bundle header offset is invalid.'
		}
		$reader.BaseStream.Position = $headerOffset
		$majorVersion = $reader.ReadUInt32()
		$minorVersion = $reader.ReadUInt32()
		$fileCount = $reader.ReadInt32()
		if (($majorVersion -ne $expectedBundleMajorVersion) -or
			($minorVersion -ne $expectedBundleMinorVersion) -or
			($fileCount -lt 1) -or ($fileCount -gt $maxBundleFileCount)) {
			throw 'Previous updater bundle manifest version or file count is unsupported.'
		}

		$null = $reader.ReadString()
		for ($headerField = 0; $headerField -lt 4; $headerField++) {
			$null = $reader.ReadInt64()
		}
		$null = $reader.ReadUInt64()

		$assemblyEntry = $null
		for ($fileIndex = 0; $fileIndex -lt $fileCount; $fileIndex++) {
			$entryOffset = $reader.ReadInt64()
			$entrySize = $reader.ReadInt64()
			$compressedSize = $reader.ReadInt64()
			$fileType = $reader.ReadByte()
			$relativePath = $reader.ReadString()
			if ($relativePath -cne 'AiUsageDashboard.Updater.dll') {
				continue
			}
			if (($null -ne $assemblyEntry) -or
				($fileType -ne [byte][Microsoft.NET.HostModel.Bundle.FileType]::Assembly)) {
				throw 'Previous updater bundle contains an invalid main assembly entry.'
			}
			$assemblyEntry = [pscustomobject] @{
				Offset = $entryOffset
				Size = $entrySize
				CompressedSize = $compressedSize
			}
		}
		if ($null -eq $assemblyEntry) {
			throw 'Previous updater bundle has no main assembly entry.'
		}

		$storedSize = if ($assemblyEntry.CompressedSize -gt 0) {
			$assemblyEntry.CompressedSize
		}
		else {
			$assemblyEntry.Size
		}
		if (($assemblyEntry.Size -lt 1) -or
			($assemblyEntry.Size -gt $maxMainAssemblyBytes) -or
			($storedSize -lt 1) -or
			($storedSize -gt $maxMainAssemblyBytes) -or
			($assemblyEntry.Offset -lt 0) -or
			($assemblyEntry.Offset -gt ($headerOffset - $storedSize))) {
			throw 'Previous updater main assembly entry is outside the supported bundle bounds.'
		}

		$reader.BaseStream.Position = $assemblyEntry.Offset
		$storedBytes = $reader.ReadBytes([int] $storedSize)
		if ($storedBytes.Length -ne $storedSize) {
			throw 'Previous updater main assembly entry is truncated.'
		}
		if ($assemblyEntry.CompressedSize -eq 0) {
			return ,$storedBytes
		}

		$compressedStream = [IO.MemoryStream]::new($storedBytes, $false)
		$assemblyStream = [IO.MemoryStream]::new()
		try {
			$decompressor = [IO.Compression.DeflateStream]::new(
				$compressedStream,
				[IO.Compression.CompressionMode]::Decompress)
			try {
				$buffer = [byte[]]::new($decompressionBufferBytes)
				while (($readCount = $decompressor.Read($buffer, 0, $buffer.Length)) -gt 0) {
					if (($assemblyStream.Length + $readCount) -gt $assemblyEntry.Size) {
						throw 'Previous updater main assembly exceeds its declared size.'
					}
					$assemblyStream.Write($buffer, 0, $readCount)
				}
			}
			finally {
				$decompressor.Dispose()
			}
			if ($assemblyStream.Length -ne $assemblyEntry.Size) {
				throw 'Previous updater main assembly does not match its declared size.'
			}
			return ,$assemblyStream.ToArray()
		}
		finally {
			$assemblyStream.Dispose()
			$compressedStream.Dispose()
		}
	}
	finally {
		$reader.Dispose()
	}
}

function Assert-ReusedUpdaterUpdateConfiguration {
	param(
		[Parameter(Mandatory = $true)]
		[string] $ExecutablePath,

		[Parameter(Mandatory = $true)]
		[string] $ExpectedFeedUrl,

		[Parameter(Mandatory = $true)]
		[string] $ExpectedChannel,

		[Parameter(Mandatory = $true)]
		[string] $ExpectedTrustedKeysFile
	)

	$assembly = [Reflection.Assembly]::Load(
		(Read-BundledUpdaterAssembly -ExecutablePath $ExecutablePath))
	$metadata = @($assembly.GetCustomAttributes(
		[Reflection.AssemblyMetadataAttribute],
		$false))
	foreach ($expectedMetadata in @(
		@('AiUsageDashboard.UpdateFeedUrl', $ExpectedFeedUrl),
		@('AiUsageDashboard.UpdateChannel', $ExpectedChannel))) {
		$matches = @($metadata | Where-Object {
			$_.Key -ceq $expectedMetadata[0]
		})
		if (($matches.Count -ne 1) -or
			($matches[0].Value -cne $expectedMetadata[1])) {
			throw "Previous updater metadata '$($expectedMetadata[0])' does not match this release."
		}
	}

	$resourceName = 'AiUsageDashboard.UpdateTrustedKeys.json'
	$matchingResources = @($assembly.GetManifestResourceNames() |
		Where-Object { $_ -ceq $resourceName })
	if ($matchingResources.Count -ne 1) {
		throw 'Previous updater does not contain exactly one embedded trust store.'
	}
	$resourceStream = $assembly.GetManifestResourceStream($resourceName)
	if ($null -eq $resourceStream) {
		throw 'Previous updater embedded trust store cannot be opened.'
	}
	$resourceBuffer = [IO.MemoryStream]::new()
	try {
		$resourceStream.CopyTo($resourceBuffer)
		$embeddedTrustBytes = $resourceBuffer.ToArray()
	}
	finally {
		$resourceBuffer.Dispose()
		$resourceStream.Dispose()
	}
	$expectedTrustBytes = [IO.File]::ReadAllBytes($ExpectedTrustedKeysFile)
	if (!([Collections.StructuralComparisons]::StructuralEqualityComparer.Equals(
			$embeddedTrustBytes,
			$expectedTrustBytes))) {
		throw 'Previous updater embedded trust store does not byte-match this release.'
	}
}

function Assert-ReusedUpdaterSource {
	param(
		[Parameter(Mandatory = $true)]
		[string] $PreviousPackageVersion,

		[Parameter(Mandatory = $true)]
		[string] $PreviousPackageSourceRevision,

		[Parameter(Mandatory = $true)]
		[string] $PreviousUpdaterVersion,

		[Parameter(Mandatory = $true)]
		[string] $PreviousUpdaterSourceRevision,

		[Parameter(Mandatory = $true)]
		[string] $CurrentSourceRevision
	)

	$originUrl = @(& git -C $repositoryRoot remote get-url origin)
	if (($LASTEXITCODE -ne 0) -or ($originUrl.Count -ne 1) -or
		($originUrl[0] -cnotin @(
			'https://github.com/Sokaka/ai-usage-dashboard',
			'https://github.com/Sokaka/ai-usage-dashboard.git'))) {
		throw 'Reusing a formal updater requires the canonical public repository origin.'
	}

	foreach ($source in @(
		[pscustomobject] @{ Version = $PreviousPackageVersion; Revision = $PreviousPackageSourceRevision },
		[pscustomobject] @{ Version = $PreviousUpdaterVersion; Revision = $PreviousUpdaterSourceRevision })) {
		$releaseVersion = $source.Version
		$releaseSourceRevision = $source.Revision
		if ($releaseSourceRevision -cnotmatch '^[0-9a-f]{40}$') {
			throw "Previous v$releaseVersion source revision is not a full release commit."
		}

		$tagRef = "refs/tags/v$releaseVersion"
		$tagRefs = @(& git -C $repositoryRoot ls-remote --tags origin $tagRef "$tagRef^{}")
		if (($LASTEXITCODE -ne 0) -or ($tagRefs.Count -eq 0)) {
			throw "Unable to verify official v$releaseVersion tag."
		}
		$directTagSource = $null
		$peeledTagSource = $null
		foreach ($tagLine in $tagRefs) {
			$tagParts = $tagLine -split '\s+'
			if (($tagParts.Count -ne 2) -or ($tagParts[0] -cnotmatch '^[0-9a-f]{40}$')) {
				throw "Official v$releaseVersion tag response is invalid."
			}
			if ($tagParts[1] -ceq $tagRef) {
				$directTagSource = $tagParts[0]
			}
			elseif ($tagParts[1] -ceq "$tagRef^{}") {
				$peeledTagSource = $tagParts[0]
			}
			else {
				throw "Official v$releaseVersion tag response contains an unexpected ref."
			}
		}
		$tagSource = if ($null -ne $peeledTagSource) {
			$peeledTagSource
		}
		else {
			$directTagSource
		}
		if ($tagSource -cne $releaseSourceRevision) {
			throw "Previous v$releaseVersion tag does not match its signed source revision."
		}

		& git -C $repositoryRoot merge-base --is-ancestor `
			$releaseSourceRevision $CurrentSourceRevision
		if ($LASTEXITCODE -ne 0) {
			throw "Previous v$releaseVersion source is not an ancestor of the current source."
		}
	}

	$updaterSourcePaths = @(
		'src/AiUsageDashboard.Updater',
		'src/AiUsageDashboard.Updater.Core',
		'src/AiUsageDashboard.Licensing',
		'tools/Publish-Updater.ps1',
		'tools/Assert-LicensePayload.ps1',
		'Directory.Build.props',
		'Directory.Build.targets',
		'Directory.Packages.props',
		'global.json',
		'NuGet.Config'
	)
	& git -C $repositoryRoot diff --quiet `
		$PreviousUpdaterSourceRevision $CurrentSourceRevision -- $updaterSourcePaths
	if ($LASTEXITCODE -ne 0) {
		throw 'Updater, Core, Licensing, or build sources changed since the reused updater was built.'
	}

	$uncommittedUpdaterInputs = @(& git -C $repositoryRoot status `
		--porcelain=v1 --untracked-files=all -- $updaterSourcePaths)
	if (($LASTEXITCODE -ne 0) -or ($uncommittedUpdaterInputs.Count -ne 0)) {
		throw 'Updater, Core, Licensing, or build sources have uncommitted changes.'
	}
}

try {
	New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
	New-Item -ItemType Directory -Path $bundleRoot | Out-Null
	$sourceRevisionOutput = @(& git -C $repositoryRoot rev-parse HEAD)
	if (($LASTEXITCODE -ne 0) -or
		($sourceRevisionOutput.Count -ne 1) -or
		($sourceRevisionOutput[0] -notmatch '^[0-9a-fA-F]{40}$')) {
		throw 'Unable to resolve the source revision for update metadata.'
	}

	$currentSourceRevision = ([string] $sourceRevisionOutput[0]).ToLowerInvariant()
	$sourceRevision = $currentSourceRevision
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

	$previousFeed = $null
	if ($reusePreviousUpdater) {
		if (($Channel -cne 'stable') -or
			($FeedUrl -cne 'https://github.com/Sokaka/ai-usage-dashboard/releases/latest/download/AiUsageDashboard-update-stable.json') -or
			($AssetBaseUrl -cne "https://github.com/Sokaka/ai-usage-dashboard/releases/download/v$Version")) {
			throw 'Reusing a previous updater requires the canonical public stable feed and release URLs.'
		}

		$resolvedPreviousFeedPath = (Resolve-Path -LiteralPath $PreviousSignedFeedPath).Path
		$resolvedPreviousUpdaterPath = (Resolve-Path -LiteralPath $PreviousUpdaterPath).Path
		foreach ($inputPath in @($resolvedPreviousFeedPath, $resolvedPreviousUpdaterPath)) {
			$inputFile = Get-Item -LiteralPath $inputPath -Force
			if ($inputFile.PSIsContainer -or
				(($inputFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
				throw "Previous release input is not an ordinary file: $inputPath"
			}
		}
		if ((Split-Path -Leaf $resolvedPreviousFeedPath) -cne $feedName) {
			throw "Previous signed feed filename must be $feedName."
		}

		$previousFeedCopyPath = Join-Path $workingRoot 'previous-signed-feed.json'
		$previousPayloadPath = Join-Path $workingRoot 'previous-verified-payload.json'
		[IO.File]::Copy($resolvedPreviousFeedPath, $previousFeedCopyPath)
		& dotnet run --project $signingProject --configuration Release -- verify `
			--feed $previousFeedCopyPath --trust $resolvedTrustedKeysFile `
			--channel stable --output $previousPayloadPath
		if ($LASTEXITCODE -ne 0) {
			throw "Previous release feed signature verification failed: $resolvedPreviousFeedPath"
		}

		$previousEnvelope = Get-Content -LiteralPath $previousFeedCopyPath -Raw |
			ConvertFrom-Json
		if ($previousEnvelope.signerKeyId -cne $SigningKeyId) {
			throw 'The reused updater must continue using the previous feed signing key.'
		}
		$previousFeed = Get-Content -LiteralPath $previousPayloadPath -Raw |
			ConvertFrom-Json
		$previousPackageVersion = [string] $previousFeed.package.version
		$previousUpdaterVersion = [string] $previousFeed.updater.version
		if (($previousFeed.releaseSequence -gt $PreviousReleaseSequence) -or
			([Management.Automation.SemanticVersion]::Parse($previousPackageVersion).CompareTo($publishedVersion) -ge 0) -or
			([Management.Automation.SemanticVersion]::Parse($previousUpdaterVersion).CompareTo($publishedVersion) -ge 0) -or
			([Management.Automation.SemanticVersion]::Parse($previousUpdaterVersion).CompareTo(
				[Management.Automation.SemanticVersion]::Parse($previousPackageVersion)) -gt 0)) {
			throw 'Previous release sequence or App/Updater versions are incompatible with this release.'
		}

		$previousReleaseBaseUrl =
			"https://github.com/Sokaka/ai-usage-dashboard/releases/download/v$previousPackageVersion"
		if (($previousFeed.package.downloadUrl -cne
				"$previousReleaseBaseUrl/$($previousFeed.package.fileName)") -or
			($previousFeed.updater.downloadUrl -cne
				"$previousReleaseBaseUrl/$($previousFeed.updater.fileName)")) {
			throw 'Previous signed feed must point to the official versioned Release assets.'
		}

		$updaterName = [string] $previousFeed.updater.fileName
		if ((Split-Path -Leaf $resolvedPreviousUpdaterPath) -cne $updaterName) {
			throw "Previous updater filename does not match the signed feed: $resolvedPreviousUpdaterPath"
		}
		if (![string]::IsNullOrWhiteSpace($MinimumUpdaterVersion) -and
			($MinimumUpdaterVersion -cne $previousUpdaterVersion)) {
			throw 'MinimumUpdaterVersion must equal the reused updater version.'
		}
		$MinimumUpdaterVersion = $previousUpdaterVersion
		Assert-ReusedUpdaterSource `
			-PreviousPackageVersion $previousPackageVersion `
			-PreviousPackageSourceRevision $previousFeed.package.sourceRevision `
			-PreviousUpdaterVersion $previousUpdaterVersion `
			-PreviousUpdaterSourceRevision $previousFeed.updater.sourceRevision `
			-CurrentSourceRevision $currentSourceRevision
	}

	$artifactNames = @(
		$packageName,
		"$packageName.sha256",
		$updaterName,
		"$updaterName.sha256",
		$feedName,
		"$feedName.sha256"
	)

	foreach ($artifactName in $artifactNames) {
		$finalPath = Join-Path $resolvedOutputRoot $artifactName
		if (Test-Path -LiteralPath $finalPath) {
			throw "Update bundle artifact already exists: $finalPath"
		}
	}

	$publishArguments = @{
		Version = $Version
		OutputRoot = $bundleRoot
	}
	if ($AllowDirtyTreeForVerification) {
		$publishArguments['AllowDirtyTreeForVerification'] = $true
	}
	if ($reusePreviousUpdater) {
		$updaterPath = Join-Path $bundleRoot $updaterName
		[IO.File]::Copy($resolvedPreviousUpdaterPath, $updaterPath)
		$updaterInfo = Get-Item -LiteralPath $updaterPath
		$actualUpdaterHash = (Get-FileHash -LiteralPath $updaterPath -Algorithm SHA256).Hash.ToLowerInvariant()
		$previousUpdaterProductVersion =
			[Diagnostics.FileVersionInfo]::GetVersionInfo($updaterPath).ProductVersion
		if (($updaterInfo.Length -ne $previousFeed.updater.sizeBytes) -or
			($actualUpdaterHash -cne $previousFeed.updater.sha256) -or
			($previousUpdaterProductVersion -cne
				"$previousUpdaterVersion+$($previousFeed.updater.sourceRevision)")) {
			throw 'Previous updater bytes or ProductVersion do not match the signed feed.'
		}
		Assert-ReusedUpdaterUpdateConfiguration `
			-ExecutablePath $updaterPath `
			-ExpectedFeedUrl $FeedUrl `
			-ExpectedChannel $Channel `
			-ExpectedTrustedKeysFile $resolvedTrustedKeysFile

		$probeRoot = Join-Path $workingRoot 'current-updater-probe'
		$probeArguments = @{
			Version = $Version
			OutputRoot = $probeRoot
		}
		if ($AllowDirtyTreeForVerification) {
			$probeArguments['AllowDirtyTreeForVerification'] = $true
		}
		& (Join-Path $PSScriptRoot 'Publish-Updater.ps1') `
			@probeArguments `
			-FeedUrl $FeedUrl `
			-Channel $Channel `
			-TrustedKeysFile $resolvedTrustedKeysFile
		$probeExecutablePath = Join-Path $probeRoot "AiUsageDashboard-Updater-$Version-$runtimeIdentifier.exe"

		$previousExportRoot = Join-Path $workingRoot 'previous-installer-licenses'
		$currentExportRoot = Join-Path $workingRoot 'current-installer-licenses'
		Export-InstallerLicenses -ExecutablePath $updaterPath -ExportRoot $previousExportRoot
		Export-InstallerLicenses -ExecutablePath $probeExecutablePath -ExportRoot $currentExportRoot
		Assert-InstallerExportsEqual `
			-PreviousExportRoot $previousExportRoot `
			-CurrentExportRoot $currentExportRoot
		"$actualUpdaterHash  $updaterName" |
			Set-Content -LiteralPath "$updaterPath.sha256" -Encoding ascii -NoNewline
	}

	& (Join-Path $PSScriptRoot 'Publish-Internal.ps1') `
		@publishArguments `
		-FeedUrl $FeedUrl `
		-Channel $Channel `
		-TrustedKeysFile $resolvedTrustedKeysFile
	if (!$reusePreviousUpdater) {
		& (Join-Path $PSScriptRoot 'Publish-Updater.ps1') `
			@publishArguments `
			-FeedUrl $FeedUrl `
			-Channel $Channel `
			-TrustedKeysFile $resolvedTrustedKeysFile
	}

	$packagePath = Join-Path $bundleRoot $packageName
	$updaterPath = Join-Path $bundleRoot $updaterName
	$packageHash = Read-CanonicalChecksum -ArtifactPath $packagePath
	$updaterHash = Read-CanonicalChecksum -ArtifactPath $updaterPath

	$packageInfo = Get-Item -LiteralPath $packagePath
	$updaterInfo = Get-Item -LiteralPath $updaterPath
	$updaterVersion = if ($reusePreviousUpdater) {
		$previousUpdaterVersion
	}
	else {
		$Version
	}
	$updaterSourceRevision = if ($reusePreviousUpdater) {
		[string] $previousFeed.updater.sourceRevision
	}
	else {
		$sourceRevision
	}
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
			version = $updaterVersion
			runtimeIdentifier = $runtimeIdentifier
			fileName = $updaterName
			downloadUrl = "$AssetBaseUrl/$updaterName"
			sizeBytes = $updaterInfo.Length
			sha256 = $updaterHash
			sourceRevision = $updaterSourceRevision
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
