#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'versioning.ps1')
. (Join-Path $PSScriptRoot 'release-ref-validation.ps1')
$checks = 0
function Check([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }; $script:checks++
}
function Reject([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null; } catch { $script:checks++; return }; throw $Message
}
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$fixture = Join-Path $artifacts ('release-infrastructure-tests-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixture)
function Invoke-FixtureGit([string[]]$Arguments) {
    & git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git operation failed.' }
}
try {
    Invoke-FixtureGit @('init','-q','-b','main')
    Invoke-FixtureGit @('commit','-q','--allow-empty','-m','source')
    Invoke-FixtureGit @('tag','-a','fixture-v0.0.6','-m','old release')
    Invoke-FixtureGit @('commit','-q','--allow-empty','-m','next source')
    Invoke-FixtureGit @('tag','-a','fixture-v0.0.7','-m','formal release')
    Assert-AnnotatedReleaseAncestry $fixture 'fixture-v0.0.7' 'main'; $checks++
    Invoke-FixtureGit @('commit','-q','--allow-empty','-m','catalog and documentation')
    Assert-AnnotatedReleaseAncestry $fixture 'fixture-v0.0.7' 'main'; $checks++
    Invoke-FixtureGit @('tag','lightweight')
    Reject { Assert-AnnotatedReleaseAncestry $fixture 'lightweight' 'main' } 'A lightweight tag was accepted.'
    $tagObject = (& git -C $fixture rev-parse refs/tags/fixture-v0.0.7).Trim()
    Assert-MatchingRemoteReleaseTag $fixture 'fixture-v0.0.7' ([pscustomobject]@{ type='tag'; sha=$tagObject }); $checks++
    Reject { Assert-MatchingRemoteReleaseTag $fixture 'fixture-v0.0.7' ([pscustomobject]@{ type='tag'; sha=('F'*40) }) } 'A conflicting remote tag was accepted.'
    Reject { Assert-MatchingRemoteReleaseTag $fixture 'fixture-v0.0.7' ([pscustomobject]@{ type='commit'; sha=$tagObject }) } 'An unannotated remote tag was accepted.'
    Reject { Assert-MatchingRemoteReleaseTag $fixture 'fixture-v0.0.7' ([pscustomobject]@{}) } 'An incomplete remote ref was accepted.'
    Invoke-FixtureGit @('switch','-q','--orphan','unrelated')
    Invoke-FixtureGit @('commit','-q','--allow-empty','-m','unrelated source')
    Invoke-FixtureGit @('tag','-a','outside','-m','unrelated tag')
    Reject { Assert-AnnotatedReleaseAncestry $fixture 'outside' 'main' } 'A tag outside release branch history was accepted.'
    Invoke-FixtureGit @('switch','-q','main')

    $historyPath = Join-Path $fixture 'delivery-versions.json'
    $history = @{ schemaVersion=1; deliveries=@(@('0.0.7','0.0.8','0.0.9','0.1.0','0.1.1') | ForEach-Object {
        @{ tagPrefix='fixture-v'; version=$_; evidence='local test delivery' }
    }) }
    [IO.File]::WriteAllText($historyPath, ($history | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Assert-NextReleaseVersion $fixture '0.1.2' -TagPattern 'fixture-v*' -TagPrefix 'fixture-v'; $checks++
    Reject { Assert-NextReleaseVersion $fixture '0.0.8' -TagPattern 'fixture-v*' -TagPrefix 'fixture-v' } 'Formal numbering ignored later local deliveries.'
    Reject { Assert-NextReleaseVersion $fixture '0.1.1' -TagPattern 'fixture-v*' -TagPrefix 'fixture-v' } 'A consumed local version was reused.'
    Reject { Assert-NextReleaseVersion $fixture '0.1.3' -TagPattern 'fixture-v*' -TagPrefix 'fixture-v' } 'A new release skipped an unconsumed number.'
    Assert-NextReleaseVersion $fixture '0.0.7' -TagPattern 'fixture-v*' -TagPrefix 'fixture-v' -AllowExistingTag; $checks++
    Invoke-FixtureGit @('tag','-a','fixture-v0.1.2','-m','formal after local deliveries')
    Assert-NextReleaseVersion $fixture '0.1.2' -TagPattern 'fixture-v*' -TagPrefix 'fixture-v' -AllowExistingTag; $checks++
    $package = Join-Path $fixture 'artifacts/local-package.zip'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($package))
    [IO.File]::WriteAllText($package,'isolated package evidence')
    Add-LocalDeliveryVersion $fixture 'fixture-v' '0.1.3' $package
    $delivered = @(Get-LocalDeliveryVersions $fixture 'fixture-v')
    Check ($delivered.Count -eq 6 -and $delivered[-1].Text -eq '0.1.3') 'A local package did not consume its version.'
    $before = (Get-FileHash -LiteralPath $historyPath).Hash
    Reject { Add-LocalDeliveryVersion $fixture 'fixture-v' '0.1.3' $package } 'A duplicate local delivery was recorded.'
    Check ((Get-FileHash -LiteralPath $historyPath).Hash -ceq $before) 'Rejected local delivery changed the history.'
    Check (@(Get-LocalDeliveryVersions $fixture 'other-v').Count -eq 0) 'Delivery counters leaked across projects.'
    Assert-ReleaseVersionContract; $checks++
    $history.deliveries += $history.deliveries[0]
    [IO.File]::WriteAllText($historyPath, ($history | ConvertTo-Json -Depth 8))
    Reject { Get-LocalDeliveryVersions $fixture 'fixture-v' } 'Duplicate/corrupt history silently reset the counter.'
    Write-Host "Release infrastructure: $checks checks passed; isolated Git/history fixtures, no remote operation."
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^release-infrastructure-tests-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup.' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
