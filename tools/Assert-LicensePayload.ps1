[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $ExecutablePath,

	[Parameter(Mandatory = $true)]
	[ValidateSet('app', 'setup', 'updater', 'capture', 'installer')]
	[string] $Profile,

	[Parameter(Mandatory = $true)]
	[string] $ExpectedManifestPath,

	[Parameter(Mandatory = $true)]
	[string] $WorkingRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resolvedWorkingRoot = (Resolve-Path -LiteralPath $WorkingRoot).Path
$exportRoot = Join-Path $resolvedWorkingRoot ('.aud-license-probe-' + [Guid]::NewGuid().ToString('N'))
$process = [Diagnostics.Process]::new()

try {
	$process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
	$process.StartInfo.FileName = (Resolve-Path -LiteralPath $ExecutablePath).Path
	$process.StartInfo.UseShellExecute = $false
	$process.StartInfo.CreateNoWindow = $true
	$process.StartInfo.RedirectStandardOutput = $true
	$process.StartInfo.RedirectStandardError = $true
	$process.StartInfo.ArgumentList.Add('--export-licenses')
	$process.StartInfo.ArgumentList.Add($exportRoot)
	if (!$process.Start()) { throw "Unable to start license export probe: $ExecutablePath" }
	$stdout = $process.StandardOutput.ReadToEndAsync()
	$stderr = $process.StandardError.ReadToEndAsync()
	if (!$process.WaitForExit(30000)) {
		$process.Kill($true)
		$process.WaitForExit()
		$null = $stdout.GetAwaiter().GetResult()
		$null = $stderr.GetAwaiter().GetResult()
		throw "License export probe timed out: $ExecutablePath"
	}
	$null = $stdout.GetAwaiter().GetResult()
	$exportError = $stderr.GetAwaiter().GetResult()
	if ($process.ExitCode -ne 0) {
		throw "License export probe failed for $ExecutablePath (exit $($process.ExitCode)): $exportError"
	}

	$expected = Get-Content -LiteralPath $ExpectedManifestPath -Raw | ConvertFrom-Json
	$exportedManifestPath = Join-Path $exportRoot 'third-party-notices/component-manifest.json'
	$actual = Get-Content -LiteralPath $exportedManifestPath -Raw | ConvertFrom-Json
	$expectedComponents = @($expected.components | Where-Object { $_.profiles -ccontains $Profile })
	$expectedDocumentIds = @($expectedComponents.documents | Sort-Object -Unique)
	$expectedDocuments = @($expected.documents | Where-Object { $expectedDocumentIds -ccontains $_.id })
	$expectedDocumentPaths = @($expectedDocuments.path | Sort-Object -Unique)
	$actualDocumentPaths = @($actual.documents.path | Sort-Object -Unique)
	if (($expectedComponents.Count -eq 0) -or ($expectedDocumentPaths.Count -eq 0) -or
		($expectedDocuments.Count -ne $expectedDocumentIds.Count) -or
		($actual.schemaVersion -ne $expected.schemaVersion) -or
		($actual.termsVersion -cne $expected.termsVersion) -or
		(Compare-Object @($expectedComponents.id | Sort-Object) @($actual.components.id | Sort-Object)) -or
		(Compare-Object $expectedDocumentPaths $actualDocumentPaths)) {
		throw "Exported license profile '$Profile' does not match the source component manifest: $ExecutablePath"
	}
	$allowedPaths = @($expectedDocumentPaths) + @('third-party-notices/component-manifest.json', 'ACCEPTANCE-DIGEST.txt')
	$rootPrefix = [IO.Path]::GetFullPath($exportRoot) + [IO.Path]::DirectorySeparatorChar
	$actualPaths = @(Get-ChildItem -LiteralPath $exportRoot -File -Recurse | ForEach-Object {
		[IO.Path]::GetRelativePath($exportRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
	})
	if (Compare-Object @($allowedPaths | Sort-Object) @($actualPaths | Sort-Object)) {
		throw "Exported license payload has missing or unexpected files: $ExecutablePath"
	}
	foreach ($component in $expectedComponents) {
		$actualComponent = @($actual.components | Where-Object { $_.id -ceq $component.id })
		if (($actualComponent.Count -ne 1) -or ($actualComponent[0].version -cne $component.version) -or
			(Compare-Object @($component.documents | Sort-Object) @($actualComponent[0].documents | Sort-Object))) {
			throw "Exported license component version differs for '$($component.id)': $ExecutablePath"
		}
	}
	foreach ($document in $expectedDocuments) {
			$documentPath = [IO.Path]::GetFullPath((Join-Path $exportRoot $document.path))
			if (!$documentPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
				throw "License document escapes the probe directory: $($document.path)"
			}
			$documentInfo = Get-Item -LiteralPath $documentPath -Force
			if (($documentInfo.Length -eq 0) -or
				(($documentInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
				((Get-FileHash -LiteralPath $documentPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $document.sha256)) {
				throw "Exported license document hash or file type differs: $($document.path)"
			}
			$actualDocument = @($actual.documents | Where-Object { $_.id -ceq $document.id })
			if (($actualDocument.Count -ne 1) -or ($actualDocument[0].path -cne $document.path) -or
				($actualDocument[0].sha256 -cne $document.sha256) -or
				($actualDocument[0].source -cne $document.source) -or
				($actualDocument[0].readable -ne $document.readable)) {
				throw "Exported license manifest digest differs: $($document.path)"
			}
	}
	$digest = [IO.File]::ReadAllText((Join-Path $exportRoot 'ACCEPTANCE-DIGEST.txt')).Trim()
	if ($digest -cnotmatch '^[0-9a-f]{64}$') { throw "Exported acceptance digest is invalid: $ExecutablePath" }
	Write-Host "Verified $Profile license export: $($expectedDocumentPaths.Count) documents from $ExecutablePath"
}
finally {
	$process.Dispose()
	if (Test-Path -LiteralPath $exportRoot) {
		$resolvedExportRoot = [IO.Path]::GetFullPath($exportRoot)
		$expectedPrefix = [IO.Path]::GetFullPath($resolvedWorkingRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
		if (!$resolvedExportRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
			!(Split-Path -Leaf $resolvedExportRoot).StartsWith('.aud-license-probe-', [StringComparison]::Ordinal)) {
			throw "Refusing to remove a license probe outside its owned working root: $resolvedExportRoot"
		}
		foreach ($entry in @(Get-Item -LiteralPath $resolvedExportRoot -Force) + @(Get-ChildItem -LiteralPath $resolvedExportRoot -Force -Recurse)) {
			if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
				throw "Refusing to remove a license probe containing a reparse point: $($entry.FullName)"
			}
		}
		Remove-Item -LiteralPath $resolvedExportRoot -Recurse -Force
	}
}
