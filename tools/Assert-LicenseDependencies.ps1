[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $DepsPath,

	[Parameter(Mandatory = $true)]
	[ValidateSet('app', 'setup', 'claude-capture')]
	[string] $Profile,

	[Parameter(Mandatory = $true)]
	[string] $ExpectedManifestPath,

	[switch] $SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$deps = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $ExpectedManifestPath -Raw | ConvertFrom-Json
$components = @($manifest.components | Where-Object { $_.profiles -ccontains $Profile })
$nonDependencyComponentIds = @('dashboard', 'microsoft-terms')
$expectedDependencies = @($components | Where-Object {
	($nonDependencyComponentIds -cnotcontains $_.id) -and
	($SelfContained -or !$_.name.StartsWith('Microsoft.NETCore.App.Runtime.', [StringComparison]::Ordinal)) -and
	($SelfContained -or !$_.name.StartsWith('Microsoft.WindowsDesktop.App.Runtime.', [StringComparison]::Ordinal))
})
$seenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($library in $deps.libraries.PSObject.Properties) {
	$separator = $library.Name.LastIndexOf('/')
	if ($separator -le 0) { throw "Invalid dependency identity '$($library.Name)' in $DepsPath" }
	$name = $library.Name.Substring(0, $separator)
	$version = $library.Name.Substring($separator + 1)
	if (($library.Value.type -ceq 'project') -and $name.StartsWith('AiUsageDashboard.', [StringComparison]::Ordinal)) {
		continue
	}
	if ($name.StartsWith('runtimepack.', [StringComparison]::Ordinal)) {
		$name = $name.Substring('runtimepack.'.Length)
	}
	$component = @($components | Where-Object { $_.name -ceq $name })
	if (($component.Count -ne 1) -or ($component[0].version -cne $version)) {
		throw "Dependency '$($library.Name)' in $DepsPath is missing or has a different version in the '$Profile' license manifest."
	}
	if (!$seenNames.Add($name)) { throw "Duplicate dependency '$name' in $DepsPath" }
}
foreach ($component in $expectedDependencies) {
	if (!$seenNames.Contains($component.name)) {
		throw "License component '$($component.name)/$($component.version)' is missing from published dependencies: $DepsPath"
	}
}
if ($Profile -ceq 'app') {
	& (Join-Path $PSScriptRoot 'Assert-NoBundledProviderCli.ps1') `
		-AppRoot ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($DepsPath)))
}
Write-Host "Verified $Profile license dependency versions: $($seenNames.Count) libraries from $DepsPath"
