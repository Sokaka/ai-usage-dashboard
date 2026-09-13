[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $AppRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($AppRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$rootInfo = Get-Item -LiteralPath $root -Force
if (!$rootInfo.PSIsContainer -or
	(($rootInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
	throw "The App package root must be a regular directory: $root"
}
$allowedExecutables = @(
	'AiUsageDashboard.App.exe',
	'AiUsageDashboard.Antigravity.Setup.exe',
	'AiUsageDashboard.AntigravityCapture.exe',
	'AiUsageDashboard.ClaudeCapture.exe',
	'createdump.exe'
)
$executableExtensions = @('.exe', '.com', '.cmd', '.bat', '.ps1', '.sh', '.js', '.mjs', '.cjs', '.node')
$providerRuntimeNames = @(
	'copilot', 'claude', 'codex', 'grok', 'agy', 'antigravity', 'node', 'npm', 'npx',
	'copilot_runtime.dll', 'libcopilot_runtime.so', 'libcopilot_runtime.dylib',
	'GitHub-Copilot-CLI-LICENSE.md'
)
$directories = [Collections.Generic.Stack[string]]::new()
$directories.Push($root)
$fileCount = 0
while ($directories.Count -gt 0) {
	$directory = $directories.Pop()
	foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
		if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
			throw "The App package contains a reparse point: $($entry.FullName)"
		}
		if ($entry.PSIsContainer) {
			$directories.Push($entry.FullName)
			continue
		}
		$fileCount++
		$relativePath = $entry.FullName.Substring($root.Length + 1)
		$isApprovedExecutable = ($directory -ieq $root) -and
			($allowedExecutables -icontains $entry.Name)
		if ((($executableExtensions -icontains $entry.Extension) -and !$isApprovedExecutable) -or
			($providerRuntimeNames -icontains $entry.Name)) {
			throw "Unexpected executable or provider CLI package content detected: $relativePath"
		}
	}
}
Write-Host "Verified App package executable allowlist: $fileCount files from $root"
