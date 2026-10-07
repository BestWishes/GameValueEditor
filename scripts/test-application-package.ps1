#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'application-package.ps1')
$script:failBuild = $false
$script:failCandidate = $false
$script:collisionPath = ''
$script:checks = 0
function Build-ApplicationBinaries {
    param([string]$Root, [string]$Work)
    if ($script:failBuild) { throw 'Injected build failure.' }
    foreach ($project in @('GameValueEditor','GameValueEditor.Updater')) {
        [IO.Directory]::CreateDirectory((Join-Path $Work $project)) | Out-Null
        [IO.File]::WriteAllText((Join-Path $Work "$project/$project.exe"), 'fixture binary')
    }
}
function Test-ApplicationCandidate {
    param($Archive, [string]$Version, [string]$Commit, [string]$Work)
    Assert-OfflineEntries $Archive @('GameValueEditor.exe','GameValueEditor.Updater.exe','README.md','LICENSE','SECURITY.md','release-compatibility.json')
    $capability = (Read-OfflineEntryText $Archive 'release-compatibility.json') | ConvertFrom-Json
    Check ($capability.Version -ceq $Version -and $capability.Compatibility.MinimumModuleHostApi -eq 2 -and
        $capability.Compatibility.MaximumModuleHostApi -eq 8 -and $capability.Compatibility.MaximumCatalogSchemaVersion -eq 5) `
        'Candidate capability declaration does not match the current API 8 host'
    if ($script:failCandidate) { throw 'Injected candidate failure.' }
    if ($script:collisionPath) { [IO.File]::WriteAllText($script:collisionPath, 'concurrent package') }
}
function Check([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:checks++
}
function MustFail([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -match $Pattern) "Expected $Pattern, received $message"
}
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$fixture = Assert-OfflinePath (Join-Path $artifacts ('application-package-tests-' + [guid]::NewGuid().ToString('N'))) $artifacts
try {
    [IO.Directory]::CreateDirectory((Join-Path $fixture 'dist')) | Out-Null
    foreach ($name in @('README.md','LICENSE','SECURITY.md')) { [IO.File]::WriteAllText((Join-Path $fixture $name), $name) }
    $old = Join-Path $fixture 'dist/GameValueEditor-v0.4.8-win-x64.zip'
    $offline = Join-Path $fixture 'dist/GameValueEditor-v0.4.9-complete-offline-win-x64.zip'
    [IO.File]::WriteAllText($old, 'old valid bytes'); [IO.File]::WriteAllText($offline, 'offline bytes')
    $output = Join-Path $fixture 'dist/GameValueEditor-v0.4.9-win-x64.zip'
    $argsForFixture = @{ Root = $fixture; Version = '0.4.9'; Commit = ('a' * 40); OutputPath = $output }
    $script:failBuild = $true
    MustFail { Invoke-ApplicationPackage @argsForFixture } 'Injected build'
    Check (-not [IO.File]::Exists($output) -and [IO.File]::ReadAllText($old) -ceq 'old valid bytes') 'Build failure removed an old package'
    $script:failBuild = $false; $script:failCandidate = $true
    MustFail { Invoke-ApplicationPackage @argsForFixture } 'Injected candidate'
    Check (-not [IO.File]::Exists($output) -and [IO.File]::ReadAllText($offline) -ceq 'offline bytes') 'Candidate failure removed an offline package'
    $script:failCandidate = $false; $script:collisionPath = $output
    MustFail { Invoke-ApplicationPackage @argsForFixture } '.'
    Check ([IO.File]::ReadAllText($output) -ceq 'concurrent package') 'Promotion overwrote a concurrently created package'
    $script:collisionPath = ''
    MustFail { Invoke-ApplicationPackage @argsForFixture } 'immutable'
    $argsForFixture.OutputPath = Join-Path $fixture 'dist/review.zip'
    $success = Invoke-ApplicationPackage @argsForFixture
    Check ($success.Verified -and [IO.File]::Exists($success.ArchivePath)) 'Verified candidate was not promoted'
    Check ([IO.File]::ReadAllText($old) -ceq 'old valid bytes' -and [IO.File]::ReadAllText($offline) -ceq 'offline bytes') 'Success removed other archives'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'dist') -Filter '.application-*' -File).Count -eq 0) 'Temporary ZIP leaked'
    Write-Host "Application packaging regressions passed: $script:checks assertions."
} finally {
    if ([IO.Directory]::Exists($fixture)) {
        $null = Assert-OfflinePath $fixture $artifacts
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
