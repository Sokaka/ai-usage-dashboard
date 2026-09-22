param(
  [Parameter(Mandatory = $true)] [string] $WorkflowPath,
  [Parameter(Mandatory = $true)] [string] $RequestPath
)

$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json -AsHashtable
$fixtureRoot = Split-Path -Parent $RequestPath
$script:FixtureRemoteMutationCount = 0
$script:FixtureReleaseViewReadCount = 0
$script:FixtureReleaseApiPaths = [Collections.Generic.List[string]]::new()
$env:PATH = Join-Path $fixtureRoot 'no-native-commands'
$env:RUNNER_TEMP = $fixtureRoot
$env:GITHUB_REPOSITORY = 'fixture/release-candidate'
$env:GITHUB_RUN_ID = '12345'
$env:GITHUB_RUN_ATTEMPT = [string] $request.BuildAttempt
$env:GITHUB_SHA = 'a' * 40
$env:GITHUB_REF = 'refs/heads/main'
$env:RELEASE_VERSION = '0.2.0'
$env:EXPECTED_SHA = $env:GITHUB_SHA
$env:DRAFT_CANDIDATE = 'true'
$env:CREATE_DRAFT_RELEASE = if ($request.CreateDraftRelease) { 'true' } else { 'false' }
$env:SEQUENCE_AUDIT = 'true'
$env:RELEASE_SEQUENCE = '2'
$env:PREVIOUS_RELEASE_SEQUENCE = '1'
$env:GITHUB_OUTPUT = Join-Path $fixtureRoot 'build-output.txt'
$env:GITHUB_STEP_SUMMARY = Join-Path $fixtureRoot 'summary.md'
$script:FixtureRemoteMainSha = if ($request.RemoteMainSha) { $request.RemoteMainSha } else { $env:GITHUB_SHA }

function Get-WorkflowScript([string] $StepName) {
  $lines = [IO.File]::ReadAllLines($WorkflowPath)
  $matches = @(0..($lines.Length - 1) | Where-Object {
    $lines[$_] -ceq "      - name: $StepName"
  })
  if ($matches.Count -ne 1) {
    throw "Expected one workflow step '$StepName' in $WorkflowPath; found $($matches.Count)."
  }

  $runIndex = $matches[0] + 1
  while (($runIndex -lt $lines.Length) -and ($lines[$runIndex] -cne '        run: |')) {
    if ($lines[$runIndex] -match '^      - name:') {
      throw "Workflow step '$StepName' has no literal PowerShell block."
    }
    $runIndex++
  }
  $body = [Collections.Generic.List[string]]::new()
  for ($index = $runIndex + 1; $index -lt $lines.Length; $index++) {
    if ([string]::IsNullOrWhiteSpace($lines[$index])) {
      $body.Add('')
    }
    elseif ($lines[$index].StartsWith('          ')) {
      $body.Add($lines[$index].Substring(10))
    }
    else {
      break
    }
  }
  $source = $body -join [Environment]::NewLine
  if ([string]::IsNullOrWhiteSpace($source) -or $source.Contains('${{')) {
    throw "Workflow step '$StepName' is empty or needs unsupported expression evaluation."
  }
  return [ScriptBlock]::Create($source)
}

function git {
  $global:LASTEXITCODE = 0
  switch ($args -join ' ') {
    'rev-parse HEAD' { return $env:GITHUB_SHA }
    'ls-remote origin refs/heads/main' { return "$script:FixtureRemoteMainSha`trefs/heads/main" }
    'ls-remote origin refs/tags/v0.2.0' { return }
    'tag --list v0.2.0' { return }
    'status --porcelain=v1 --untracked-files=all' { return }
    default { throw "Unexpected git call in workflow fixture: $($args -join ' ')" }
  }
}

function gh {
  $global:LASTEXITCODE = 0
  if (($args.Count -eq 2) -and ($args[0] -ceq 'api')) {
    if ($args[1].StartsWith("repos/$env:GITHUB_REPOSITORY/releases/", [StringComparison]::Ordinal)) {
      $script:FixtureReleaseApiPaths.Add($args[1])
    }
    switch ($args[1]) {
      "repos/$env:GITHUB_REPOSITORY" {
        return @{ id = 6789; private = $request.RepositoryPrivate } | ConvertTo-Json
      }
      "repos/$env:GITHUB_REPOSITORY/releases/3456" {
        return Get-ReleaseApiResponse -BaseResponse $script:FixtureRelease -Behavior $request.FreezeRead
      }
    }
  }
  elseif (($args[0] -ceq 'release') -and ($args[1] -ceq 'create')) {
    $script:FixtureRemoteMutationCount++
    return
  }
  elseif (($args[0] -ceq 'release') -and ($args[1] -ceq 'view')) {
    $script:FixtureReleaseViewReadCount++
    $view = @{
      databaseId = $script:FixtureRelease.id
      isDraft = $true
      isPrerelease = $true
      tagName = $script:FixtureRelease.tag_name
      targetCommitish = $script:FixtureRelease.target_commitish
      assets = @($script:FixtureRelease.assets | ForEach-Object {
        @{
          id = "RA_kwDOUQKKmc4hg9X$($_.id)"
          apiUrl = "https://api.github.com/repos/$env:GITHUB_REPOSITORY/releases/assets/$($_.id)"
          name = $_.name
          size = $_.size
          digest = $_.digest
        }
      })
      url = $script:FixtureRelease.html_url
    }
    $jsonIndex = [Array]::IndexOf($args, '--json')
    if (($jsonIndex -lt 0) -or ($jsonIndex + 1 -ge $args.Count)) {
      throw 'Fixture release view requires an explicit JSON field list.'
    }
    $selectedView = @{}
    $requestedFields = @($args[$jsonIndex + 1]) | ForEach-Object { ([string] $_).Split(',') }
    foreach ($field in $requestedFields) {
      if (!$view.ContainsKey($field)) {
        throw "Unsupported fixture release view field: $field"
      }
      $selectedView[$field] = $view[$field]
    }
    return Get-ReleaseApiResponse -BaseResponse $selectedView -Behavior $request.ReleaseView
  }
  throw "Unexpected gh call in workflow fixture: $($args -join ' ')"
}

function Get-ReleaseApiResponse([hashtable] $BaseResponse, [hashtable] $Behavior) {
  $response = $BaseResponse.Clone()
  $response.assets = @($BaseResponse.assets | ForEach-Object { $_.Clone() })
  if ($null -ne $Behavior.Overrides) {
    foreach ($entry in $Behavior.Overrides.GetEnumerator()) {
      $response[$entry.Key] = $entry.Value
    }
  }
  if ($null -ne $Behavior.AssetOverrides) {
    $assetIndex = 0
    if ($Behavior.AssetName) {
      $assetIndex = -1
      for ($index = 0; $index -lt $response.assets.Count; $index++) {
        if ($response.assets[$index].name -ceq $Behavior.AssetName) {
          $assetIndex = $index
          break
        }
      }
      if ($assetIndex -lt 0) {
        throw "Unknown fixture asset name: $($Behavior.AssetName)"
      }
    }
    foreach ($entry in $Behavior.AssetOverrides.GetEnumerator()) {
      $response.assets[$assetIndex][$entry.Key] = $entry.Value
    }
  }
  if ($Behavior.OmitAsset) {
    $response.assets = @($response.assets | Select-Object -Skip 1)
  }
  switch ($Behavior.Failure) {
    'exit-code' { $global:LASTEXITCODE = 1 }
    'malformed-json' { return '{ invalid-json' }
    $null { }
    default { throw "Unsupported fixture release API failure: $($Behavior.Failure)" }
  }
  return $response | ConvertTo-Json -Depth 5
}

# 驗簽與 GitHub transport 是此測試的外部邊界；checksum 與凍結 script 仍實際執行。
function dotnet {
  $outputIndex = [Array]::IndexOf($args, '--output')
  if (($args[0] -cne 'run') -or ($args -cnotcontains 'verify') -or
      ($outputIndex -lt 0) -or ($outputIndex + 1 -ge $args.Count)) {
    throw "Unexpected dotnet call in workflow fixture: $($args -join ' ')"
  }
  [IO.File]::WriteAllText($args[$outputIndex + 1], ($script:FixtureFeed | ConvertTo-Json -Depth 5))
  $global:LASTEXITCODE = 0
}

function Initialize-ReleaseFiles {
  $releaseRoot = Join-Path $fixtureRoot 'internal-release'
  New-Item -ItemType Directory -Path $releaseRoot | Out-Null
  $packageName = 'AiUsageDashboard-0.2.0-win-x64.zip'
  $updaterName = 'AiUsageDashboard-Updater-0.2.0-win-x64.exe'
  $feedName = 'AiUsageDashboard-update-stable.json'
  foreach ($name in @($packageName, $updaterName, $feedName)) {
    $path = Join-Path $releaseRoot $name
    [IO.File]::WriteAllText($path, "fixture bytes for $name")
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$path.sha256", "$hash  $name")
  }
  $script:FixtureFeed = @{
    schemaVersion = 1
    channel = 'stable'
    releaseSequence = 2
    minimumUpdaterVersion = '0.2.0'
  }
  foreach ($entry in @(@{ key = 'package'; name = $packageName }, @{ key = 'updater'; name = $updaterName })) {
    $path = Join-Path $releaseRoot $entry.name
    $script:FixtureFeed[$entry.key] = @{
      version = '0.2.0'
      fileName = $entry.name
      sizeBytes = (Get-Item -LiteralPath $path).Length
      sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
      sourceRevision = $env:GITHUB_SHA
    }
  }
  $assetId = 0
  $script:FixtureRelease = @{
    id = 3456
    html_url = "https://github.com/$env:GITHUB_REPOSITORY/releases/tag/v0.2.0"
    draft = $true
    prerelease = $true
    tag_name = 'v0.2.0'
    target_commitish = $env:GITHUB_SHA
    assets = @(Get-ChildItem -LiteralPath $releaseRoot -File | ForEach-Object {
      $assetId++
      @{
        id = $assetId
        name = $_.Name
        size = $_.Length
        digest = 'sha256:' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        browser_download_url = "https://github.com/$env:GITHUB_REPOSITORY/releases/download/untagged-fixture-uuid/$($_.Name)"
      }
    })
  }
}

$phase = 'source'
$failure = $null
$buildOutputs = @()
$releaseOutputPath = Join-Path $fixtureRoot 'release-output.txt'
try {
  & (Get-WorkflowScript 'Validate release source') | Out-Null
  $buildOutputs = @([IO.File]::ReadAllLines($env:GITHUB_OUTPUT))
  $outputValues = @{}
  foreach ($line in $buildOutputs) {
    $parts = $line.Split('=', 2)
    $outputValues.Add($parts[0], $parts[1])
  }
  $env:BUILD_RUN_ID = $outputValues.build_run_id
  $env:BUILD_RUN_ATTEMPT = $outputValues.build_run_attempt
  $env:RELEASE_VERSION = $outputValues.candidate_version
  $env:GITHUB_OUTPUT = $releaseOutputPath
  $env:GITHUB_RUN_ATTEMPT = [string] $request.PublishAttempt
  if ($request.RemoteMainShaAtFreeze) {
    $script:FixtureRemoteMainSha = $request.RemoteMainShaAtFreeze
  }
  $phase = 'freeze'
  Initialize-ReleaseFiles
  & (Get-WorkflowScript 'Reverify and freeze draft candidate')
  $phase = 'complete'
}
catch {
  $failure = $_.Exception.Message
}

$notesPath = Join-Path $fixtureRoot 'draft-candidate-notes.md'
$receiptPath = Join-Path $fixtureRoot 'candidate-freeze.json'
$result = @{
  Phase = $phase
  Error = $failure
  RemoteMutationCount = $script:FixtureRemoteMutationCount
  ReleaseViewReadCount = $script:FixtureReleaseViewReadCount
  ReleaseApiPaths = @($script:FixtureReleaseApiPaths)
  BuildOutputs = $buildOutputs
  ReleaseOutputs = @(if (Test-Path -LiteralPath $releaseOutputPath) { [IO.File]::ReadAllLines($releaseOutputPath) })
  Notes = if (Test-Path -LiteralPath $notesPath) { [IO.File]::ReadAllText($notesPath) } else { $null }
  Receipt = if (Test-Path -LiteralPath $receiptPath) { Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } else { $null }
}
[IO.File]::WriteAllText((Join-Path $fixtureRoot 'result.json'), ($result | ConvertTo-Json -Depth 8))
