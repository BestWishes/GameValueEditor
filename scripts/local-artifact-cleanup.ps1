# Local maintenance only: bounded ZIP inventory, no Git/network/build hooks.
. (Join-Path $PSScriptRoot 'offline-bundle.ps1')

function Get-LocalArchiveRow {
    param([IO.FileInfo]$File, [string]$Root)
    $path = Assert-OfflinePath $File.FullName $Root
    $before = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $after = Get-Item -LiteralPath $path
    if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc.Ticks -ne $after.LastWriteTimeUtc.Ticks) {
        throw "Archive changed during inventory: $path"
    }
    return [pscustomobject]@{ Path = $path; Name = $File.Name; Length = $after.Length
        ModifiedTicks = $after.LastWriteTimeUtc.Ticks.ToString(); Sha256 = $hash
        Action = 'Keep'; Reason = 'Unknown or unverified archive'; Category = 'Unknown'; Version = [version]'0.0.0'; Complete = $false }
}

function Get-LocalOfflineReceipt {
    param([string]$Root, $Row)
    $path = Get-OfflineReceiptPath $Root $Row.Sha256
    if (-not [IO.File]::Exists($path)) { return $null }
    $receipt = (Read-OfflineJsonFile $path) | ConvertFrom-Json -AsHashtable
    if ($receipt.SchemaVersion -ne 1 -or $receipt.Sha256 -cne $Row.Sha256 -or $receipt.SizeBytes -ne $Row.Length -or
        $receipt.Files -isnot [Collections.IDictionary] -or $receipt.Modules -isnot [array] -or
        $receipt.Complete -isnot [bool]) { throw "Invalid receipt: $path" }
    $null = Assert-OfflineVersion $receipt.ApplicationVersion
    $archive = $null
    try {
        $archive = Open-OfflineArchive $Row.Path
        if ($archive.Hash -cne $Row.Sha256) { throw 'Archive changed while checking receipt.' }
        Assert-OfflineEntries $archive @($receipt.Files.Keys)
        foreach ($name in $receipt.Files.Keys) {
            if ((Get-OfflineEntryHash $archive $name) -cne $receipt.Files[$name]) { throw "Receipt content differs: $name" }
        }
        if ($receipt.Modules.Count -eq 0 -or -not $receipt.Files.Contains('GameValueEditor.exe') -or
            -not $receipt.Files.Contains('data/modules/installed.json')) { throw 'Not an application/module bundle.' }
        return $receipt
    } finally { Close-OfflineArchive $archive }
}

function Test-LocalHistoricalHost {
    param($Row, [version]$OldestRetained)
    if (-not (Test-StandardApplicationArchiveName $Row.Name)) { return $false }
    $version = [version]([regex]::Match($Row.Name, '-v([0-9]+\.[0-9]\.[0-9])-').Groups[1].Value)
    if ($version -ge $OldestRetained) { return $false }
    $archive = $null
    try {
        $archive = Open-OfflineArchive $Row.Path
        $metadata = (Read-OfflineEntryText $archive 'release-compatibility.json') | ConvertFrom-Json -AsHashtable
        return $archive.Hash -ceq $Row.Sha256 -and $metadata.SchemaVersion -eq 1 -and
            $metadata.Version -ceq $version.ToString() -and $archive.Entries.ContainsKey('GameValueEditor.exe') -and
            $archive.Entries.ContainsKey('GameValueEditor.Updater.exe') -and
            @($archive.Entries.Keys | Where-Object { $_ -like 'data/*' }).Count -eq 0
    } catch { return $false }
    finally { Close-OfflineArchive $archive }
}

function Get-LocalArtifactCleanupPlan {
    [CmdletBinding()]
    param([string]$ApplicationRepository, [string]$ModuleRepository = '', [switch]$IncludeModuleArchives)
    $root = [IO.Path]::GetFullPath($ApplicationRepository)
    $dist = Assert-OfflinePath (Join-Path $root 'dist') $root
    $indexPath = Assert-OfflinePath (Join-Path $root 'release-index.json') $root
    $index = (Read-OfflineJsonFile $indexPath) | ConvertFrom-Json -AsHashtable
    if ($index.schemaVersion -ne 1 -or $index.releases -isnot [array] -or $index.releases.Count -eq 0) { throw 'Invalid host release index.' }
    $protected = @{}
    foreach ($record in $index.releases) {
        $null = Assert-OfflineVersion $record.version
        if (-not (Test-StandardApplicationArchiveName $record.assetName) -or
            $record.assetName -cne "GameValueEditor-v$($record.version)-win-x64.zip" -or
            $record.sha256 -cnotmatch '^[0-9A-F]{64}$' -or $record.sizeBytes -le 0 -or $protected.ContainsKey($record.assetName)) {
            throw 'Invalid/duplicate host retention record.'
        }
        $protected[$record.assetName] = $record
    }
    $oldest = @($index.releases | ForEach-Object { [version]$_.version } | Sort-Object)[0]
    $rows = [Collections.Generic.List[object]]::new()
    # These are old tooling output directories, not a recursive directory search.
    foreach ($directory in @($dist, (Join-Path $dist 'modules'), (Join-Path $dist 'offline-tool-validation'))) {
        $null = Assert-OfflinePath $directory $root
        if (-not [IO.Directory]::Exists($directory)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $directory -File -Filter '*.zip') {
            $row = Get-LocalArchiveRow $file $dist
            if ($directory -eq $dist -and $protected.ContainsKey($row.Name)) {
                $record = $protected[$row.Name]
                if ($row.Sha256 -cne $record.sha256 -or $row.Length -ne $record.sizeBytes) { throw "Protected release differs from index: $($row.Path)" }
                $row.Category = 'PublishedHost'; $row.Reason = 'Retained host release index input'
            } elseif (Test-LocalHistoricalHost $row $oldest) {
                $row.Category = 'HistoricalHost'; $row.Action = 'Delete'; $row.Reason = 'Older than retained host releases'
            } elseif ($row.Name -cmatch '^GameValueEditor-(?:v(?:0|[1-9][0-9]*)\.[0-9]\.[0-9](?:-[A-Za-z0-9.-]+)?|local-review-[A-Za-z0-9.-]+)\.zip$') {
                $receipt = Get-LocalOfflineReceipt $root $row
                if ($null -ne $receipt) {
                    $row.Category = 'OfflineBundle'; $row.Version = [version]$receipt.ApplicationVersion
                    $row.Complete = $receipt.Complete
                    $row.Reason = 'Verified offline bundle'
                }
            }
            $rows.Add($row)
        }
    }
    $offline = @($rows | Where-Object Category -eq 'OfflineBundle' | Sort-Object `
        @{Expression = { $_.Version }; Descending = $true},
        @{Expression = { [long]$_.ModifiedTicks }; Descending = $true}, Path)
    # The newest full bundle always occupies one of the three slots, so newer
    # selected-module recipes cannot evict the only complete distribution.
    $latestComplete = @($offline | Where-Object Complete | Select-Object -First 1)
    $ordered = @($latestComplete) + @($offline | Where-Object { $_ -notin $latestComplete })
    $hashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $distinct = 0
    foreach ($row in $ordered) {
        if (-not $hashes.Add($row.Sha256)) { $row.Action = 'Delete'; $row.Reason = 'Duplicate offline bundle content' }
        elseif (++$distinct -gt 3) { $row.Action = 'Delete'; $row.Reason = 'Outside newest three offline bundle contents' }
        else { $row.Reason = if ($row -in $latestComplete) { 'Newest complete distribution' } else { 'One of three retained offline bundle contents' } }
    }
    $catalogHash = ''
    $moduleRoot = ''
    if ($IncludeModuleArchives) {
        if (-not $ModuleRepository) { throw 'IncludeModuleArchives requires an explicit ModuleRepository.' }
        $moduleRoot = [IO.Path]::GetFullPath($ModuleRepository)
        $moduleDist = Assert-OfflinePath (Join-Path $moduleRoot 'dist') $moduleRoot
        $catalogPath = Assert-OfflinePath (Join-Path $moduleRoot 'catalog.json') $moduleRoot
        $catalog = (Read-OfflineJsonFile $catalogPath) | ConvertFrom-Json -AsHashtable
        if ($catalog.schemaVersion -ne 5 -or $catalog.modules -isnot [array]) { throw 'Invalid module catalog.' }
        $catalogHash = (Get-FileHash -LiteralPath $catalogPath).Hash
        $assets = @{}; $families = @{}
        foreach ($module in $catalog.modules) {
            $snapshots = @($module) + @($module.releases)
            foreach ($snapshot in $snapshots) {
                $null = Assert-OfflineVersion $snapshot.version
                $name = [Uri]::UnescapeDataString([IO.Path]::GetFileName(([Uri]$snapshot.downloadUrl).AbsolutePath))
                Assert-OfflineSegment $name
                $assetMatch = [regex]::Match($name, '^(?<prefix>GameValueEditor\.Module\.[A-Za-z0-9]+)-v(?<version>(?:0|[1-9][0-9]*)\.[0-9]\.[0-9])\.zip$')
                if (-not $assetMatch.Success -or
                    $assetMatch.Groups['version'].Value -cne $snapshot.version -or $snapshot.sha256 -cnotmatch '^[0-9A-F]{64}$' -or $snapshot.sizeBytes -le 0) {
                    throw 'Invalid module archive record.'
                }
                $prefix = $assetMatch.Groups['prefix'].Value
                if ($assets.ContainsKey($name) -and ($assets[$name].sha256 -cne $snapshot.sha256 -or $assets[$name].sizeBytes -ne $snapshot.sizeBytes)) {
                    throw 'Conflicting module archive records.'
                }
                $assets[$name] = $snapshot
                if (-not $families.ContainsKey($prefix)) { $families[$prefix] = @{ Id = $module.id; Oldest = [version]$snapshot.version } }
                if ($families[$prefix].Id -cne $module.id) { throw 'Module archive family belongs to multiple IDs.' }
                if ([version]$snapshot.version -lt $families[$prefix].Oldest) { $families[$prefix].Oldest = [version]$snapshot.version }
            }
        }
        if ([IO.Directory]::Exists($moduleDist)) {
            foreach ($file in Get-ChildItem -LiteralPath $moduleDist -File -Filter '*.zip') {
                $row = Get-LocalArchiveRow $file $moduleDist
                if ($assets.ContainsKey($row.Name)) {
                    if ($row.Sha256 -cne $assets[$row.Name].sha256 -or $row.Length -ne $assets[$row.Name].sizeBytes) { throw "Protected module differs: $($row.Path)" }
                    $row.Category = 'PublishedModule'; $row.Reason = 'Retained module catalog input'
                } elseif ($row.Name -cmatch '^(?<prefix>GameValueEditor\.Module\.[A-Za-z0-9]+)-v(?<version>(?:0|[1-9][0-9]*)\.[0-9]\.[0-9])\.zip$' -and
                    $families.ContainsKey($matches.prefix) -and [version]$matches.version -lt $families[$matches.prefix].Oldest) {
                    $family = $families[$matches.prefix]; $version = $matches.version
                    $archive = $null
                    try {
                        $archive = Open-OfflineArchive $row.Path
                        $manifest = (Read-OfflineEntryText $archive 'module.json') | ConvertFrom-Json -AsHashtable
                        if ($archive.Hash -ceq $row.Sha256 -and $manifest.id -ceq $family.Id -and $manifest.version -ceq $version) {
                            $row.Category = 'HistoricalModule'; $row.Action = 'Delete'; $row.Reason = 'Older than retained module catalog versions'
                        }
                    } catch { $row.Reason = 'Unverified historical module; preserved' }
                    finally { Close-OfflineArchive $archive }
                }
                $rows.Add($row)
            }
        }
    }
    $delete = @($rows | Where-Object Action -eq 'Delete')
    [pscustomobject]@{ ApplicationRepository = $root; ModuleRepository = $moduleRoot; IncludeModuleArchives = [bool]$IncludeModuleArchives
        IndexSha256 = (Get-FileHash -LiteralPath $indexPath).Hash; CatalogSha256 = $catalogHash
        Rows = @($rows | Sort-Object Path); DeleteCount = $delete.Count; DeleteBytes = [long](($delete | Measure-Object Length -Sum).Sum ?? 0) }
}

function Invoke-LocalArtifactCleanup {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param([Parameter(Mandatory)]$Plan)
    # Recompute the same bounded plan. Changes to receipts, index, catalog or archives
    # invalidate an old decision; never apply a stale hand-edited target list.
    $fresh = Get-LocalArtifactCleanupPlan -ApplicationRepository $Plan.ApplicationRepository `
        -ModuleRepository $Plan.ModuleRepository -IncludeModuleArchives:$Plan.IncludeModuleArchives
    $oldJson = $Plan | ConvertTo-Json -Depth 8 -Compress
    if ($oldJson -cne ($fresh | ConvertTo-Json -Depth 8 -Compress)) { throw 'Cleanup plan changed; preview again before applying.' }
    [long]$freed = 0; $deleted = 0
    foreach ($row in $fresh.Rows | Where-Object Action -eq 'Delete') {
        $allowed = if ($row.Category -like '*Module' -and $fresh.IncludeModuleArchives) { Join-Path $fresh.ModuleRepository 'dist' }
            else { Join-Path $fresh.ApplicationRepository 'dist' }
        $null = Assert-OfflinePath $row.Path $allowed
        $current = Get-LocalArchiveRow (Get-Item -LiteralPath $row.Path) $allowed
        if ($current.Sha256 -cne $row.Sha256 -or $current.Length -ne $row.Length -or $current.ModifiedTicks -cne $row.ModifiedTicks) {
            throw "Cleanup target changed: $($row.Path)"
        }
        if ($PSCmdlet.ShouldProcess($row.Path, 'Permanently remove obsolete generated ZIP')) {
            Remove-Item -LiteralPath $row.Path -Force
            $freed += $row.Length; $deleted++
        }
    }
    [pscustomobject]@{ DeletedCount = $deleted; FreedBytes = $freed; WhatIf = [bool]$WhatIfPreference }
}
