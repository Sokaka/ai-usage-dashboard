[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $DepsPath,

	[Parameter(Mandatory = $true)]
	[ValidateSet('app', 'setup')]
	[string] $Profile,

	[Parameter(Mandatory = $true)]
	[string] $ExpectedManifestPath,

	[string] $ProjectPath,

	[switch] $SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$deps = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $ExpectedManifestPath -Raw | ConvertFrom-Json
$components = @($manifest.components | Where-Object { $_.profiles -ccontains $Profile })
$nonDependencyComponentIds = @('dashboard', 'microsoft-terms', 'copilot-cli')
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
	if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
		throw 'The App dependency check requires ProjectPath to inspect the effective CopilotCliVersion build property.'
	}
	$cliComponent = @($components | Where-Object { $_.id -ceq 'copilot-cli' })
	if ($cliComponent.Count -ne 1) { throw 'The App license manifest must identify exactly one Copilot CLI version.' }
	$propertyOutput = @(& dotnet msbuild $ProjectPath -getProperty:CopilotCliVersion -nologo)
	if (($LASTEXITCODE -ne 0) -or ($propertyOutput.Count -ne 1) -or
		([string] $propertyOutput[0] -cne $cliComponent[0].version)) {
		throw "The effective CopilotCliVersion build property does not match the license manifest: $ProjectPath"
	}
	$cliPath = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($DepsPath))) 'runtimes/win-x64/native/copilot.exe'
	$cliInfo = Get-Item -LiteralPath $cliPath -Force
	$cliVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($cliInfo.FullName).ProductVersion
	if (($cliInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
		throw "The packaged Copilot CLI is a reparse point: $cliPath"
	}
	if ($cliVersion -cne $cliComponent[0].version) {
		throw "Packaged Copilot CLI version '$cliVersion' differs from manifest '$($cliComponent[0].version)': $cliPath"
	}
}
Write-Host "Verified $Profile license dependency versions: $($seenNames.Count) libraries from $DepsPath"
