$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'versioning.ps1')
. (Join-Path $PSScriptRoot 'release-retention.ps1')

function New-TestRelease([string]$Tag, [string]$Version) {
    [pscustomobject]@{
        id = $Version.Replace('.', '')
        tag_name = $Tag
        draft = $false
        prerelease = $false
        assets = @([pscustomobject]@{
            name = "GameValueEditor-v$Version-win-x64.zip"
            state = 'uploaded'
        })
    }
}

foreach ($count in 0..5) {
    $releases = @()
    foreach ($index in 0..($count - 1)) {
        if ($count -eq 0) { break }
        $version = "0.4.$index"
        $releases += New-TestRelease "v$version" $version
    }
    $releases += New-TestRelease 'other-v9.9.9' '9.9.9'
    $plan = Get-ReleaseRetentionPlan -Releases $releases -TagPrefix 'v' -ExpectedAssetName {
        param($version) "GameValueEditor-v$version-win-x64.zip"
    }
    if ($plan.Keep.Count -ne [Math]::Min(3, $count) -or $plan.Delete.Count -ne [Math]::Max(0, $count - 3)) {
        throw "Retention plan failed for $count releases."
    }
    if (@($plan.Delete | ForEach-Object { $_.Release.tag_name }) -contains 'other-v9.9.9') {
        throw 'Retention plan crossed the configured release group.'
    }
}
Write-Host 'Release retention simulation passed for 0 through 5 releases.'
