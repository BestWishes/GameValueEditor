Set-StrictMode -Version Latest

function Get-ReleaseRetentionPlan {
    param(
        [Parameter(Mandatory)] [object[]]$Releases,
        [Parameter(Mandatory)] [string]$TagPrefix,
        [Parameter(Mandatory)] [scriptblock]$ExpectedAssetName
    )

    $pattern = '^' + [regex]::Escape($TagPrefix) + '(?<version>(?:0|[1-9][0-9]*)\.[0-9]\.[0-9])$'
    $eligible = foreach ($release in $Releases) {
        if ([bool]$release.draft -or [bool]$release.prerelease -or [string]$release.tag_name -notmatch $pattern) { continue }
        $versionText = [string]$matches.version
        $version = ConvertTo-ReleaseVersion $versionText
        $assetName = & $ExpectedAssetName $versionText
        $assets = @($release.assets | Where-Object {
            [string]$_.name -ceq $assetName -and [string]$_.state -eq 'uploaded'
        })
        if ($assets.Count -ne 1) { continue }
        [pscustomobject]@{ Release = $release; Version = $version; Asset = $assets[0] }
    }
    $ordered = @($eligible | Sort-Object `
        @{ Expression = { $_.Version.Major }; Descending = $true },
        @{ Expression = { $_.Version.Minor }; Descending = $true },
        @{ Expression = { $_.Version.Patch }; Descending = $true })
    [pscustomobject]@{
        Keep = @($ordered | Select-Object -First 3)
        Delete = @($ordered | Select-Object -Skip 3)
    }
}
