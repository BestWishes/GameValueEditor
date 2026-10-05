Set-StrictMode -Version Latest

function ConvertTo-ReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    $candidate = $Value.Trim()
    if ($candidate -notmatch '^(?<major>0|[1-9][0-9]*)\.(?<minor>[0-9])\.(?<patch>[0-9])$') {
        throw "Release version must use canonical A.B.C decimal-counter form: $Value"
    }
    [pscustomobject]@{
        Text = $candidate
        Major = [int]$matches.major
        Minor = [int]$matches.minor
        Patch = [int]$matches.patch
    }
}

function Get-NextReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    $version = ConvertTo-ReleaseVersion $Value
    if ($version.Patch -lt 9) { return "$($version.Major).$($version.Minor).$($version.Patch + 1)" }
    if ($version.Minor -lt 9) { return "$($version.Major).$($version.Minor + 1).0" }
    return "$($version.Major + 1).0.0"
}

function Get-ReleaseTagVersions {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$TagPattern,
        [Parameter(Mandatory)][string]$TagPrefix
    )

    $tags = @(& git -C $RepositoryRoot tag --list $TagPattern)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read release tags.' }
    @($tags | ForEach-Object {
        if (-not $_.StartsWith($TagPrefix, [StringComparison]::Ordinal)) { return }
        $parsed = ConvertTo-ReleaseVersion $_.Substring($TagPrefix.Length)
        [pscustomobject]@{ Tag = $_; Version = $parsed }
    } | Sort-Object @{ Expression = { $_.Version.Major } }, @{ Expression = { $_.Version.Minor } }, @{ Expression = { $_.Version.Patch } })
}

function Assert-NextReleaseVersion {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$Version,
        [string]$TagPattern = 'v*',
        [string]$TagPrefix = 'v',
        [switch]$AllowExistingTag
    )

    $candidate = ConvertTo-ReleaseVersion $Version
    $releases = @(Get-ReleaseTagVersions $RepositoryRoot $TagPattern $TagPrefix)
    $currentIndex = -1
    for ($index = 0; $index -lt $releases.Count; $index++) {
        if ($releases[$index].Version.Text -eq $candidate.Text) { $currentIndex = $index; break }
    }
    if ($currentIndex -ge 0) {
        if (-not $AllowExistingTag) { throw "Release tag already exists: $TagPrefix$Version" }
        if ($currentIndex -eq 0) { return }
        $previous = $releases[$currentIndex - 1].Version.Text
        $expected = Get-NextReleaseVersion $previous
        if ($candidate.Text -ne $expected) {
            throw "Release $Version skips the required version after $previous; expected $expected."
        }
        return
    }
    if ($releases.Count -eq 0) { return }
    $latest = $releases[-1].Version.Text
    $expected = Get-NextReleaseVersion $latest
    if ($candidate.Text -ne $expected) {
        throw "Next release after $latest must be $expected, not $Version."
    }
}

function Assert-ReleaseVersionContract {
    if ((Get-NextReleaseVersion '0.4.8') -ne '0.4.9' -or
        (Get-NextReleaseVersion '0.4.9') -ne '0.5.0' -or
        (Get-NextReleaseVersion '0.9.9') -ne '1.0.0') {
        throw 'Decimal-counter release carry self-test failed.'
    }
    try { $null = ConvertTo-ReleaseVersion '1.2.10' }
    catch { return }
    throw 'Decimal-counter release validation accepted a two-digit minor or patch component.'
}
