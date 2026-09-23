[CmdletBinding(DefaultParameterSetName = 'ResultsDirectory')]
param(
	[Parameter(
		Mandatory = $true,
		ParameterSetName = 'ResultsDirectory')]
	[string] $ResultsDirectory,

	[Parameter(
		Mandatory = $true,
		ParameterSetName = 'CoveragePath')]
	[string[]] $CoveragePath,

	[ValidateRange(0, 100)]
	[double] $MinimumLineCoveragePercent = 70
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$productionPackagePrefix = 'AiUsageDashboard.'
$requiredProductionPackageNames = @(
	'AiUsageDashboard.Antigravity',
	'AiUsageDashboard.Antigravity.Setup',
	'AiUsageDashboard.App',
	'AiUsageDashboard.ClaudeCapture',
	'AiUsageDashboard.Core'
)
$excludedPackageNames = @(
	'AiUsageDashboard.Tests',
	'AiUsageDashboard.TerminalFixture',
	'AiUsageDashboard.UpdaterProcessFixture',
	'AiUsageDashboard.PrivacyCheck',
	'AiUsageDashboard.AntigravitySpike'
)
$excludedTypePrefixes = @(
	'AiUsageDashboard.Tests.',
	'AiUsageDashboard.TerminalFixture.'
)
# Production-owned Antigravity source intentionally retains the
# AiUsageDashboard.AntigravitySpike namespace. Exclude the development tool
# by package name only so those production types remain in the gate.
$invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
$integerStyles = [System.Globalization.NumberStyles]::Integer

function Read-CoberturaDocument {
	param(
		[Parameter(Mandatory = $true)]
		[string] $Path
	)

	$settings = [System.Xml.XmlReaderSettings]::new()
	$settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
	$settings.XmlResolver = $null
	$reader = [System.Xml.XmlReader]::Create($Path, $settings)

	try {
		$document = [System.Xml.XmlDocument]::new()
		$document.XmlResolver = $null
		$document.Load($reader)
		return $document
	}
	finally {
		$reader.Dispose()
	}
}

function Test-ExcludedType {
	param(
		[Parameter(Mandatory = $true)]
		[string] $TypeName
	)

	foreach ($prefix in $excludedTypePrefixes) {
		if ($TypeName.StartsWith(
				$prefix,
				[System.StringComparison]::Ordinal)) {
			return $true
		}
	}

	return $false
}

$coverageFiles = if ($PSCmdlet.ParameterSetName -eq 'ResultsDirectory') {
	$resolvedResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)

	if (!(Test-Path -LiteralPath $resolvedResultsDirectory -PathType Container)) {
		throw "Coverage results directory does not exist: $resolvedResultsDirectory"
	}

	@(Get-ChildItem `
		-LiteralPath $resolvedResultsDirectory `
		-Recurse `
		-File `
		-Filter '*.cobertura.xml' |
		ForEach-Object { $_.FullName })
}
else {
	@($CoveragePath | ForEach-Object {
		$resolvedCoveragePath = [System.IO.Path]::GetFullPath($_)

		if (!(Test-Path -LiteralPath $resolvedCoveragePath -PathType Leaf)) {
			throw "Cobertura report does not exist: $resolvedCoveragePath"
		}

		$resolvedCoveragePath
	})
}
$coverageFiles = @($coverageFiles)

if ($coverageFiles.Count -eq 0) {
	throw 'No Cobertura reports were found.'
}

$coverageReports = @($coverageFiles | ForEach-Object {
	Get-FileHash -LiteralPath $_ -Algorithm SHA256
})
$uniqueReportHashes = @($coverageReports.Hash | Sort-Object -Unique)

if ($uniqueReportHashes.Count -ne 1) {
	throw (
		'Expected one logical Cobertura report, but found {0} distinct reports.' -f
			$uniqueReportHashes.Count)
}

$coverageFile = $coverageReports[0].Path
$document = Read-CoberturaDocument -Path $coverageFile
$packageNodes = @($document.SelectNodes('/coverage/packages/package'))

if ($packageNodes.Count -eq 0) {
	throw "Cobertura report contains no package data: $coverageFile"
}

$includedPackageNames = [System.Collections.Generic.HashSet[string]]::new(
	[System.StringComparer]::Ordinal)
$excludedPackagesObserved = [System.Collections.Generic.HashSet[string]]::new(
	[System.StringComparer]::Ordinal)
$coveredLineCount = 0
$validLineCount = 0

foreach ($packageNode in $packageNodes) {
	$packageName = [string] $packageNode.GetAttribute('name')

	if (!$packageName.StartsWith(
			$productionPackagePrefix,
			[System.StringComparison]::Ordinal)) {
		continue
	}

	if ($excludedPackageNames -contains $packageName) {
		$null = $excludedPackagesObserved.Add($packageName)
		continue
	}

	$packageLineCount = 0
	$classNodes = @($packageNode.SelectNodes('./classes/class'))

	foreach ($classNode in $classNodes) {
		$typeName = [string] $classNode.GetAttribute('name')

		if (Test-ExcludedType -TypeName $typeName) {
			continue
		}

		$lineNodes = @($classNode.SelectNodes('./lines/line'))

		foreach ($lineNode in $lineNodes) {
			$lineNumber = 0
			$hits = 0L
			$lineNumberText = [string] $lineNode.GetAttribute('number')
			$hitsText = [string] $lineNode.GetAttribute('hits')

			if (![int]::TryParse(
					$lineNumberText,
					$integerStyles,
					$invariantCulture,
					[ref] $lineNumber) -or
				($lineNumber -le 0) -or
				![long]::TryParse(
					$hitsText,
					$integerStyles,
					$invariantCulture,
					[ref] $hits) -or
				($hits -lt 0)) {
				throw (
					"Cobertura report contains an invalid line entry in " +
					"$packageName/${typeName}: $coverageFile")
			}

			$validLineCount++
			$packageLineCount++

			if ($hits -gt 0) {
				$coveredLineCount++
			}
		}

	}

	if ($packageLineCount -gt 0) {
		$null = $includedPackageNames.Add($packageName)
	}
}

if (($includedPackageNames.Count -eq 0) -or ($validLineCount -eq 0)) {
	throw 'Cobertura reports contain no production line coverage data.'
}

$missingRequiredPackages = @($requiredProductionPackageNames |
	Where-Object { !$includedPackageNames.Contains($_) })

if ($missingRequiredPackages.Count -gt 0) {
	throw (
		'Cobertura report is missing required production packages: {0}.' -f
			($missingRequiredPackages -join ', '))
}

$lineCoveragePercent = 100.0 * $coveredLineCount / $validLineCount
$includedPackages = @(@($includedPackageNames) | Sort-Object)
$excludedPackages = @(@($excludedPackagesObserved) | Sort-Object)

Write-Output (
	'Production line coverage: {0:N2}% ({1:N0}/{2:N0}); threshold: {3:N2}%.' -f
		$lineCoveragePercent,
		$coveredLineCount,
		$validLineCount,
		$MinimumLineCoveragePercent)
Write-Output "Included packages: $($includedPackages -join ', ')"

if ($excludedPackages.Count -gt 0) {
	Write-Output "Excluded non-production packages: $($excludedPackages -join ', ')"
}

if ($lineCoveragePercent -lt $MinimumLineCoveragePercent) {
	throw (
		'Production line coverage {0:N2}% is below the required {1:N2}%.' -f
			$lineCoveragePercent,
			$MinimumLineCoveragePercent)
}
