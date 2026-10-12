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

function Get-LocalDeliveryVersions {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$TagPrefix)

    $path = Join-Path $RepositoryRoot 'delivery-versions.json'
    if (-not (Test-Path -LiteralPath $path)) { return }
    if ((Get-Item -LiteralPath $path).Length -gt 1048576) { throw 'Delivery history is too large.' }
    $document = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($document.PSObject.Properties.Name -notcontains 'schemaVersion' -or $document.schemaVersion -ne 1 -or
        $document.PSObject.Properties.Name -notcontains 'deliveries') { throw 'Invalid delivery history.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($delivery in @($document.deliveries)) {
        if ($null -eq $delivery -or $delivery.PSObject.Properties.Name -notcontains 'tagPrefix' -or
            $delivery.PSObject.Properties.Name -notcontains 'version' -or
            $delivery.PSObject.Properties.Name -notcontains 'evidence' -or
            [string]$delivery.tagPrefix -notmatch '^(?:v|[a-z0-9-]+-v)$' -or
            [string]::IsNullOrWhiteSpace([string]$delivery.evidence)) { throw 'Invalid local delivery entry.' }
        $parsed = ConvertTo-ReleaseVersion ([string]$delivery.version)
        if (-not $seen.Add("$($delivery.tagPrefix)$($parsed.Text)")) { throw 'Duplicate local delivery version.' }
        if ($delivery.PSObject.Properties.Name -contains 'sha256' -and [string]$delivery.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw 'Invalid local delivery SHA-256.'
        }
        if ([string]$delivery.tagPrefix -ceq $TagPrefix) { $parsed }
    }
}

function Add-LocalDeliveryVersion {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$TagPrefix,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ArchivePath
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $archive = [IO.Path]::GetFullPath($ArchivePath)
    $boundary = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $archive.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($archive)) {
        throw 'Local delivery archive must exist inside its repository.'
    }
    $artifacts = Join-Path $root 'artifacts'
    $null = [IO.Directory]::CreateDirectory($artifacts)
    $lease = [IO.File]::Open((Join-Path $artifacts 'delivery-versions.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $temporary = Join-Path $root ('.delivery-versions-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        # Recheck under the exclusive lease; two builds cannot consume one number.
        Assert-NextReleaseVersion -RepositoryRoot $root -Version $Version -TagPattern "$TagPrefix*" -TagPrefix $TagPrefix
        $path = Join-Path $root 'delivery-versions.json'
        $document = if (Test-Path -LiteralPath $path) {
            Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        } else { [pscustomobject]@{ schemaVersion = 1; deliveries = @() } }
        $document.deliveries = @($document.deliveries) + @([pscustomobject]@{
            tagPrefix = $TagPrefix; version = $Version
            evidence = [IO.Path]::GetRelativePath($root, $archive).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        })
        [IO.File]::WriteAllText($temporary, ($document | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $path, $true)
    }
    finally {
        $lease.Dispose()
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
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
    $knownVersions = @(@($releases | ForEach-Object { $_.Version }) + @(Get-LocalDeliveryVersions $RepositoryRoot $TagPrefix) |
        Sort-Object Major, Minor, Patch | Group-Object Text | ForEach-Object { $_.Group[0] })
    $currentIndex = -1
    for ($index = 0; $index -lt $releases.Count; $index++) {
        if ($releases[$index].Version.Text -eq $candidate.Text) { $currentIndex = $index; break }
    }
    if ($currentIndex -ge 0) {
        if (-not $AllowExistingTag) { throw "Release tag already exists: $TagPrefix$Version" }
        $earlier = @($knownVersions | Where-Object { [version]$_.Text -lt [version]$candidate.Text })
        if ($earlier.Count -eq 0) { return }
        $previous = $earlier[-1].Text
        $expected = Get-NextReleaseVersion $previous
        if ($candidate.Text -ne $expected) {
            throw "Release $Version skips the required version after $previous; expected $expected."
        }
        return
    }
    if ($knownVersions.Count -eq 0) { return }
    $latest = $knownVersions[-1].Text
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
