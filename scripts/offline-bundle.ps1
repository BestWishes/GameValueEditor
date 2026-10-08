# Shared implementation: no download, publication or Git commands.
. (Join-Path $PSScriptRoot 'package-archive-names.ps1')
. (Join-Path $PSScriptRoot 'offline-bundle-verification.ps1')

function Assert-OfflineVersion {
    param([string]$Value)
    if ($Value -cnotmatch '^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$') { throw "Invalid decimal release version: $Value" }
    return [version]$Value
}

function Test-OfflineInteger {
    param($Value)
    return $Value -is [int] -or $Value -is [long]
}

function Assert-OfflinePath {
    param([string]$Path, [string]$Root)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escaped permitted directory: $resolved" }
    $current = $resolved
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point is not allowed: $current" }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $resolved
}

function Assert-OfflineSegment {
    param([string]$Name)
    if (-not $Name -or $Name -match '[<>:"/\\|?*\x00-\x1f]' -or $Name -match '[. ]$' -or
        $Name -in @('.', '..') -or $Name -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw "Unsafe archive/path name: $Name" }
}

function Get-OfflineStreamHash {
    param([IO.Stream]$Stream)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Stream))
}

function Get-OfflineArchiveHash {
    param($Source)
    $Source.Stream.Position = 0
    $hash = Get-OfflineStreamHash $Source.Stream
    $Source.Stream.Position = 0
    return $hash
}

function Open-OfflineArchive {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { throw "Required local ZIP is missing: $Path" }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        if ($zip.Entries.Count -gt 2048) { throw 'Archive has too many entries.' }
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name.Contains('\') -or $name.StartsWith('/') -or $name.EndsWith('/')) { throw "Unsafe archive entry: $name" }
            foreach ($segment in $name.Split('/')) { Assert-OfflineSegment $segment }
            $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixType -ne 0 -and $unixType -ne 0x8000) { throw "Non-regular ZIP entry: $name" }
            if (($entry.ExternalAttributes -band 0x410) -ne 0) { throw "Directory/reparse ZIP entry: $name" }
            $total += $entry.Length
            if ($entry.Length -gt 1GB -or $total -gt 2GB) { throw 'Archive exceeds offline bundle size limits.' }
            if (-not $entries.TryAdd($name, $entry)) { throw "Duplicate ZIP entry: $name" }
        }
        $source = @{ Path = $Path; Stream = $stream; Zip = $zip; Entries = $entries }
        $source.Hash = Get-OfflineArchiveHash $source
        return $source
    } catch {
        if ($zip) { $zip.Dispose() }
        $stream.Dispose()
        throw
    }
}

function Close-OfflineArchive {
    param($Source)
    if ($Source) { $Source.Zip.Dispose(); $Source.Stream.Dispose() }
}

function Assert-OfflineEntries {
    param($Source, [string[]]$Names)
    if ($Source.Entries.Count -ne $Names.Count) { throw "Unexpected files in ZIP: $($Source.Path)" }
    foreach ($name in $Names) {
        if (-not $Source.Entries.ContainsKey($name) -or $Source.Entries[$name].FullName -cne $name) { throw "Missing or incorrectly cased ZIP entry: $name" }
    }
}

function Read-OfflineEntryText {
    param($Source, [string]$Name)
    if (-not $Source.Entries.ContainsKey($Name)) { throw "Missing ZIP entry: $Name" }
    if ($Source.Entries[$Name].Length -gt 4MB) { throw "JSON/text entry exceeds limit: $Name" }
    $reader = [IO.StreamReader]::new($Source.Entries[$Name].Open(), [Text.UTF8Encoding]::new($false, $true))
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Read-OfflineJsonFile {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { throw "Required JSON file is missing: $Path" }
    if ((Get-Item -LiteralPath $Path).Length -gt 4MB) { throw "JSON exceeds limit: $Path" }
    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
}

function Assert-OfflineJsonSchema {
    param([string]$Json, [string]$Schema)
    $schemaText = Read-OfflineJsonFile $Schema
    if ($schemaText -match '"\$ref"\s*:\s*"(?!#)') { throw 'External JSON Schema references are not allowed.' }
    if (-not (Test-Json -Json $Json -Schema $schemaText -ErrorAction Stop)) { throw 'JSON Schema validation failed.' }
}

function Get-OfflineCanonicalJson {
    param($Value)
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary]) {
        $fields = foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) {
            ($key | ConvertTo-Json -Compress) + ':' + (Get-OfflineCanonicalJson $Value[$key])
        }
        return '{' + ($fields -join ',') + '}'
    }
    if ($Value -is [array]) {
        $items = foreach ($item in $Value) { Get-OfflineCanonicalJson $item }
        return '[' + ($items -join ',') + ']'
    }
    return ($Value | ConvertTo-Json -Compress -Depth 50)
}

function Assert-OfflineEqual {
    param($Actual, $Expected, [string]$Field)
    if ((Get-OfflineCanonicalJson $Actual) -cne (Get-OfflineCanonicalJson $Expected)) { throw "Catalog/manifest mismatch: $Field" }
}

function Assert-OfflineUnique {
    param([object[]]$Values, [string]$Field)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in $Values) {
        if (-not $seen.Add([string]$value)) { throw "Duplicate identity: $Field $value" }
    }
}

function Select-OfflineModules {
    param($Catalog, $Capability, [string]$Version, [string[]]$ModuleIds)
    if ($Catalog.schemaVersion -ne 5 -or $Catalog.schemaVersion -gt $Capability.MaximumCatalogSchemaVersion) { throw "Unsupported catalog Schema: $($Catalog.schemaVersion)" }
    if ($Catalog.hostApiVersion -lt $Capability.MinimumModuleHostApi -or $Catalog.hostApiVersion -gt $Capability.MaximumModuleHostApi) { throw 'Catalog API exceeds host capability.' }
    $hostVersion = Assert-OfflineVersion $Version
    Assert-OfflineUnique @($Catalog.modules.id) 'module ID'
    Assert-OfflineUnique $ModuleIds 'requested module ID'
    foreach ($id in $ModuleIds) { if ($id -cnotin @($Catalog.modules.id)) { throw "Unknown module ID: $id" } }
    if (@($Catalog.modules).Count -eq 0) { throw 'Catalog contains no modules.' }
    foreach ($module in $Catalog.modules) {
        Assert-OfflineSegment $module.id
        Assert-OfflineUnique @($module.releases.version) "release version of $($module.id)"
        $ordered = @($module.releases | Sort-Object { [version]$_.version } -Descending)
        if ($ordered[0].version -cne $module.version) { throw "Catalog latest version is inconsistent: $($module.id)" }
        foreach ($field in @('version','hostApiVersion','minimumHostVersion','maximumHostVersion','supportsUnlistedBuildValidation','compatibleBuilds','editors','downloadUrl','sha256','sizeBytes')) {
            Assert-OfflineEqual $module[$field] $ordered[0][$field] "$($module.id).$field"
        }
        foreach ($release in $ordered) {
            $minimum = Assert-OfflineVersion $release.minimumHostVersion
            if ($release.maximumHostVersion -and (Assert-OfflineVersion $release.maximumHostVersion) -lt $minimum) { throw "Invalid host version range: $($module.id) v$($release.version)" }
            Assert-OfflineUnique @($release.editors.id) "editor ID of $($module.id)"
        }
        if ($ModuleIds.Count -gt 0 -and $module.id -cnotin $ModuleIds) { continue }
        $compatible = @($ordered | Where-Object {
            $_.hostApiVersion -ge $Capability.MinimumModuleHostApi -and $_.hostApiVersion -le $Capability.MaximumModuleHostApi -and
            $hostVersion -ge [version]$_.minimumHostVersion -and (-not $_.maximumHostVersion -or $hostVersion -le [version]$_.maximumHostVersion)
        })
        if ($compatible.Count -eq 0) { throw "No host-compatible retained release: $($module.id)" }
        $snapshot = @{} + $module
        foreach ($key in $compatible[0].Keys) { $snapshot[$key] = $compatible[0][$key] }
        foreach ($field in @('maximumHostVersion','supportsUnlistedBuildValidation')) {
            if (-not $compatible[0].ContainsKey($field)) { $snapshot.Remove($field) }
        }
        $snapshot
    }
}

function Copy-OfflineEntry {
    param($Source, [string]$Name, [string]$Destination, [string]$Root)
    $destinationPath = Assert-OfflinePath $Destination $Root
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
    $inputStream = $Source.Entries[$Name].Open()
    $outputStream = $null
    try {
        $outputStream = [IO.File]::Open($destinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        $inputStream.CopyTo($outputStream)
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        $inputStream.Dispose()
    }
}

function Get-OfflineBinaryProductVersion {
    param([string]$Path)
    return [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
}

function Assert-OfflineHost {
    param($Source, [string]$Version, [string]$Work, $IndexRecord, [string]$ExpectedHash)
    $hostNames = @('GameValueEditor.exe','GameValueEditor.Updater.exe','README.md','LICENSE','SECURITY.md','release-compatibility.json')
    Assert-OfflineEntries $Source $hostNames
    if ($IndexRecord) {
        if ($IndexRecord.assetName -cne [IO.Path]::GetFileName($Source.Path) -or $Source.Hash -ine $IndexRecord.sha256 -or $Source.Stream.Length -ne $IndexRecord.sizeBytes) { throw 'Host ZIP does not match release-index.json.' }
        if ($ExpectedHash -and $Source.Hash -ine $ExpectedHash) { throw 'Expected host SHA-256 mismatch.' }
    } elseif ($ExpectedHash -notmatch '^[a-fA-F0-9]{64}$' -or $Source.Hash -ine $ExpectedHash) { throw 'Host ZIP has no indexed release; supply its independently verified ExpectedHostSha256.' }
    $manifest = (Read-OfflineEntryText $Source 'release-compatibility.json') | ConvertFrom-Json -AsHashtable
    $caps = $manifest.Compatibility
    if (-not (Test-OfflineInteger $manifest.SchemaVersion) -or $manifest.SchemaVersion -ne 1 -or $manifest.Version -cne $Version -or
        -not (Test-OfflineInteger $caps.MinimumModuleHostApi) -or
        -not (Test-OfflineInteger $caps.MaximumModuleHostApi) -or
        -not (Test-OfflineInteger $caps.MaximumCatalogSchemaVersion) -or
        $caps.MinimumModuleHostApi -lt 1 -or $caps.MaximumModuleHostApi -lt $caps.MinimumModuleHostApi -or $caps.MaximumCatalogSchemaVersion -lt 1) { throw 'Invalid host compatibility declaration.' }
    if ($IndexRecord) {
        foreach ($field in @('MinimumModuleHostApi','MaximumModuleHostApi','MaximumCatalogSchemaVersion')) {
            $indexField = $field.Substring(0, 1).ToLowerInvariant() + $field.Substring(1)
            if ($caps[$field] -ne $IndexRecord[$indexField]) { throw "Host/index capability mismatch: $field" }
        }
    }
    $commit = $null
    foreach ($name in @('GameValueEditor.exe','GameValueEditor.Updater.exe')) {
        $binaryPath = Join-Path $Work $name
        Copy-OfflineEntry $Source $name $binaryPath $Work
        $productVersion = Get-OfflineBinaryProductVersion $binaryPath
        if ($productVersion -cnotmatch ('^' + [regex]::Escape($Version) + '\+([a-f0-9]{40})$')) { throw "Host binary ProductVersion mismatch: $name $productVersion" }
        $binaryCommit = $Matches[1]
        if ($commit -and $commit -cne $binaryCommit) { throw 'Host and updater source commits differ.' }
        $commit = $binaryCommit
    }
    if ($IndexRecord -and $commit -cne $IndexRecord.sourceCommit) { throw 'Host/index source commit mismatch.' }
    return @{ Capability = $caps; SourceCommit = $commit; Names = $hostNames }
}

function Assert-OfflineModule {
    param($Source, $Snapshot, [string]$Schema)
    if ($Source.Hash -ine $Snapshot.sha256 -or $Source.Stream.Length -ne $Snapshot.sizeBytes) { throw "Module ZIP hash/size mismatch: $($Snapshot.id) v$($Snapshot.version)" }
    $json = Read-OfflineEntryText $Source 'module.json'
    Assert-OfflineJsonSchema $json $Schema
    $manifest = $json | ConvertFrom-Json -AsHashtable
    Assert-OfflineSegment $manifest.assemblyFile
    Assert-OfflineEntries $Source @('module.json', $manifest.assemblyFile)
    foreach ($field in @('id','version','hostApiVersion','minimumHostVersion','maximumHostVersion','displayName','gameDisplayName','processNames','legacyIds','editors','compatibleBuilds')) {
        Assert-OfflineEqual $manifest[$field] $Snapshot[$field] "$($Snapshot.id).$field"
    }
    if ([bool]$manifest.supportsUnlistedBuildValidation -ne [bool]$Snapshot.supportsUnlistedBuildValidation) { throw 'Catalog/manifest mismatch: supportsUnlistedBuildValidation' }
    return $manifest
}

function Get-OfflineEntryHash {
    param($Source, [string]$Name)
    $stream = $Source.Entries[$Name].Open()
    try { return Get-OfflineStreamHash $stream } finally { $stream.Dispose() }
}

function Write-OfflineZip {
    param([IO.Stream]$Stream, [string]$Package, [string[]]$Names)
    $zip = [IO.Compression.ZipArchive]::new($Stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        # Only the validated file graph; never recursively collect arbitrary staging files.
        foreach ($name in $Names) {
            $path = Assert-OfflinePath (Join-Path $Package $name) $Package
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $inputStream = [IO.File]::OpenRead($path)
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
}

function Assert-OfflineBundleContent {
    param($Bundle, $ExpectedFiles, $Modules)
    $names = @($ExpectedFiles.Keys) + @('data/modules/installed.json','完整离线包说明.txt')
    Assert-OfflineEntries $Bundle $names
    foreach ($name in $ExpectedFiles.Keys) {
        if ((Get-OfflineEntryHash $Bundle $name) -cne $ExpectedFiles[$name]) { throw "Bundled bytes differ: $name" }
    }
    $installed = (Read-OfflineEntryText $Bundle 'data/modules/installed.json') | ConvertFrom-Json -AsHashtable
    Assert-OfflineEqual @($installed.Keys | Sort-Object) @('Modules','SchemaVersion') 'installed.json keys'
    if (-not (Test-OfflineInteger $installed.SchemaVersion) -or $installed.SchemaVersion -ne 1 -or
        $installed.Modules -isnot [array] -or $installed.Modules.Count -ne $Modules.Count) { throw 'Invalid installed.json.' }
    Assert-OfflineUnique @($installed.Modules.Id) 'installed module ID'
    foreach ($module in $Modules) {
        $record = @($installed.Modules | Where-Object { $_.Id -ceq $module.Snapshot.id })
        if ($record.Count -ne 1 -or $record[0].Version -cne $module.Snapshot.version) { throw 'Installed identity mismatch.' }
        Assert-OfflineEqual @($record[0].Keys | Sort-Object) @('Id','InstalledUtc','Version') 'installed record keys'
        [DateTimeOffset]$timestamp = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string]$record[0].InstalledUtc, [ref]$timestamp)) { throw 'Invalid install timestamp.' }
    }
    $notice = Read-OfflineEntryText $Bundle '完整离线包说明.txt'
    if (-not $notice.Trim() -or $notice.Length -gt 4096) { throw 'Invalid offline notice.' }
}

function Test-OfflineBundleStartup {
    param($Bundle, [string]$Directory)
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    foreach ($name in $Bundle.Entries.Keys) { Copy-OfflineEntry $Bundle $name (Join-Path $Directory $name) $Directory }
    $process = Start-Process -FilePath (Join-Path $Directory 'GameValueEditor.exe') -WorkingDirectory $Directory -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Seconds 5
        $process.Refresh()
        if ($process.HasExited) { throw "Offline bundle startup failed: exit code $($process.ExitCode)" }
    } finally {
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction Stop; $process.WaitForExit() }
        $process.Dispose()
    }
}

function Assert-OfflineLocalHostEvidence {
    param([string]$Path, [string]$Root, [string]$Version, [string]$Hash)
    $resolved = Assert-OfflinePath $Path (Join-Path $Root 'artifacts')
    if ([IO.Path]::GetFileName($resolved) -cne "GameValueEditor-local-review-v$Version-win-x64.zip" -or
        $Hash -cnotmatch '^[a-fA-F0-9]{64}$') { throw 'Local host requires a review archive and explicit SHA-256, not a standard release ZIP.' }
    $noticePath = Assert-OfflinePath (Join-Path ([IO.Path]::GetDirectoryName($resolved)) 'LOCAL-REVIEW.txt') (Join-Path $Root 'artifacts')
    if (-not [IO.File]::Exists($noticePath) -or (Get-Item -LiteralPath $noticePath).Length -gt 4096) { throw 'Local host is missing its bounded LOCAL-REVIEW evidence.' }
    $notice = [IO.File]::ReadAllText($noticePath, [Text.Encoding]::UTF8)
    if (-not $notice.Contains('LOCAL ONLY / NOT A RELEASE.') -or
        -not $notice.Contains("Version $Version, base commit ") -or
        $notice -notmatch ('SHA256 ' + [regex]::Escape($Hash) + '\.')) { throw 'Local review evidence does not match the requested host.' }
    return $resolved
}

function Get-OfflineLocalModuleSnapshot {
    param($Source, [string]$Hash, [string]$Schema, $Capability, [string]$Version)
    if ($Hash -notmatch '^[a-fA-F0-9]{64}$' -or $Source.Hash -ine $Hash) { throw 'Local module SHA-256 mismatch.' }
    $json = Read-OfflineEntryText $Source 'module.json'
    Assert-OfflineJsonSchema $json $Schema
    $manifest = $json | ConvertFrom-Json -AsHashtable
    Assert-OfflineSegment $manifest.id
    $null = Assert-OfflineVersion $manifest.version
    $minimum = Assert-OfflineVersion $manifest.minimumHostVersion
    $maximum = if ($manifest.maximumHostVersion) { Assert-OfflineVersion $manifest.maximumHostVersion } else { $null }
    if (-not (Test-OfflineInteger $manifest.hostApiVersion) -or
        $manifest.hostApiVersion -lt $Capability.MinimumModuleHostApi -or $manifest.hostApiVersion -gt $Capability.MaximumModuleHostApi -or
        ($maximum -and $maximum -lt $minimum) -or [version]$Version -lt $minimum -or ($maximum -and [version]$Version -gt $maximum)) {
        throw 'Local module is incompatible with the frozen host.'
    }
    Assert-OfflineUnique @($manifest.editors.id) 'local editor ID'
    $snapshot = @{} + $manifest
    $snapshot.sha256 = $Source.Hash; $snapshot.sizeBytes = $Source.Stream.Length
    return $snapshot
}

function Invoke-OfflineBundle {
    [CmdletBinding()]
    param(
        [string]$ApplicationVersion = '',
        [string]$ApplicationRepository = (Join-Path $PSScriptRoot '..'),
        [string]$ModuleRepository = '',
        [string[]]$ModuleIds = @(),
        [string]$OutputPath = '',
        [string]$ExpectedHostSha256 = '',
        [string]$LocalHostArchivePath = '',
        [string[]]$LocalModuleArchivePaths = @(),
        [string[]]$LocalModuleSha256 = @(),
        [switch]$Force,
        [switch]$VerifyOnly
    )
    $ErrorActionPreference = 'Stop'
    if ($Force -and $VerifyOnly) { throw 'Force and VerifyOnly cannot be combined.' }
    $localOnly = $LocalHostArchivePath.Length -gt 0 -or $LocalModuleArchivePaths.Count -gt 0
    if ($LocalModuleArchivePaths.Count -ne $LocalModuleSha256.Count) { throw 'Each local module requires an explicit SHA-256.' }
    if ($localOnly -and ($Force -or -not $OutputPath -or [IO.Path]::GetFileName($OutputPath) -notmatch '-local-review-')) {
        throw 'Local review bundles require a unique explicit local-review OutputPath; Force is not allowed.'
    }
    $root = [IO.Path]::GetFullPath($ApplicationRepository)
    if (-not $ApplicationVersion) {
        $project = [xml](Get-Content -LiteralPath (Join-Path $root 'src/GameValueEditor/GameValueEditor.csproj') -Raw -Encoding UTF8)
        $ApplicationVersion = [string]$project.Project.PropertyGroup.Version
    }
    $null = Assert-OfflineVersion $ApplicationVersion
    if (-not $ModuleRepository) { $ModuleRepository = Join-Path ([IO.Path]::GetDirectoryName($root.TrimEnd('\','/'))) 'GameValueEditor-Modules' }
    $moduleRoot = [IO.Path]::GetFullPath($ModuleRepository)
    $dist = Join-Path $root 'dist'
    $artifacts = Join-Path $root 'artifacts'
    $hostPath = Assert-OfflinePath (Join-Path $dist "GameValueEditor-v$ApplicationVersion-win-x64.zip") $dist
    if ($LocalHostArchivePath) { $hostPath = Assert-OfflineLocalHostEvidence $LocalHostArchivePath $root $ApplicationVersion $ExpectedHostSha256 }
    if (-not $OutputPath) {
        $flavor = if ($ModuleIds.Count -gt 0) { 'selected-offline' } else { 'complete-offline' }
        $OutputPath = Join-Path $dist "GameValueEditor-v$ApplicationVersion-$flavor-win-x64.zip"
    }
    $output = Assert-OfflinePath $OutputPath $dist
    Assert-OfflineSegment ([IO.Path]::GetFileName($output))
    if ([IO.Path]::GetExtension($output) -cne '.zip' -or (Test-StandardApplicationArchiveName ([IO.Path]::GetFileName($output)))) { throw 'Offline output must be a ZIP, never a standard application archive.' }
    $installedForVerification = $null
    if ($VerifyOnly) {
        if (-not [IO.File]::Exists($output)) { throw "VerifyOnly requires an existing offline ZIP: $output" }
        $verified = Test-OfflineReceiptBundle $root $moduleRoot $output $ApplicationVersion $ModuleIds
        if ($null -ne $verified) { return $verified }
        $existing = Open-OfflineArchive $output
        try { $installedForVerification = (Read-OfflineEntryText $existing 'data/modules/installed.json') | ConvertFrom-Json -AsHashtable }
        finally { Close-OfflineArchive $existing }
        if (-not [IO.File]::Exists((Join-Path $moduleRoot 'catalog.json')) -or -not [IO.File]::Exists($hostPath)) {
            throw 'Missing verification evidence: restore the local receipt or original release inputs; this does not establish that the bundle is damaged.'
        }
    }
    $catalogPath = Assert-OfflinePath (Join-Path $moduleRoot 'catalog.json') $moduleRoot
    $catalogJson = Read-OfflineJsonFile $catalogPath
    Assert-OfflineJsonSchema $catalogJson (Join-Path $moduleRoot 'schemas/catalog.schema.json')
    $catalog = $catalogJson | ConvertFrom-Json -AsHashtable
    $indexRecord = $null
    $indexPath = Join-Path $root 'release-index.json'
    if (-not $LocalHostArchivePath -and [IO.File]::Exists($indexPath)) {
        $index = (Read-OfflineJsonFile $indexPath) | ConvertFrom-Json -AsHashtable
        if (-not (Test-OfflineInteger $index.schemaVersion) -or $index.schemaVersion -ne 1) { throw 'Unknown host release index schema.' }
        $matchesInIndex = @($index.releases | Where-Object { $_.version -ceq $ApplicationVersion })
        if ($matchesInIndex.Count -gt 1) { throw 'Duplicate host release index record.' }
        if ($matchesInIndex.Count -eq 1) { $indexRecord = $matchesInIndex[0] }
    }
    $work = Assert-OfflinePath (Join-Path $artifacts ('offline-build-' + [guid]::NewGuid().ToString('N'))) $artifacts
    $temporary = Assert-OfflinePath (Join-Path ([IO.Path]::GetDirectoryName($output)) ('.offline-' + [guid]::NewGuid().ToString('N') + '.zip')) $dist
    $hostSource = $null
    $bundle = $null
    $modules = [Collections.Generic.List[object]]::new()
    $workCreated = $false
    $temporaryCreated = $false
    try {
        [IO.Directory]::CreateDirectory($work) | Out-Null
        $workCreated = $true
        $hostSource = Open-OfflineArchive $hostPath
        $hostInfo = Assert-OfflineHost $hostSource $ApplicationVersion $work $indexRecord $ExpectedHostSha256
        $snapshots = if ($VerifyOnly) {
            @(Select-OfflineInstalledSnapshots $catalog $hostInfo.Capability $ApplicationVersion $installedForVerification)
        } else { @(Select-OfflineModules $catalog $hostInfo.Capability $ApplicationVersion $ModuleIds) }
        if ($VerifyOnly -and $ModuleIds.Count -gt 0) {
            Assert-OfflineEqual @($snapshots.id | Sort-Object) @($ModuleIds | Sort-Object) 'requested installed module IDs'
        }
        foreach ($snapshot in $snapshots) {
            $asset = [Uri]$snapshot.downloadUrl
            $assetName = [Uri]::UnescapeDataString([IO.Path]::GetFileName($asset.AbsolutePath))
            Assert-OfflineSegment $assetName
            $path = Assert-OfflinePath (Join-Path $moduleRoot "dist/$assetName") (Join-Path $moduleRoot 'dist')
            if ($VerifyOnly -and -not [IO.File]::Exists($path)) { throw "Missing verification evidence: original module ZIP $assetName and local receipt are unavailable." }
            $source = Open-OfflineArchive $path
            $module = @{ Source = $source; Snapshot = $snapshot; Manifest = $null }
            $modules.Add($module)
            $module.Manifest = Assert-OfflineModule $source $snapshot (Join-Path $moduleRoot 'schemas/module.schema.json')
        }
        for ($index = 0; $index -lt $LocalModuleArchivePaths.Count; $index++) {
            $localPath = Assert-OfflinePath $LocalModuleArchivePaths[$index] (Join-Path $moduleRoot 'artifacts')
            $source = Open-OfflineArchive $localPath
            $module = @{ Source = $source; Snapshot = $null; Manifest = $null }
            $modules.Add($module)
            $module.Snapshot = Get-OfflineLocalModuleSnapshot $source $LocalModuleSha256[$index] (Join-Path $moduleRoot 'schemas/module.schema.json') $hostInfo.Capability $ApplicationVersion
            $module.Manifest = Assert-OfflineModule $source $module.Snapshot (Join-Path $moduleRoot 'schemas/module.schema.json')
        }
        Assert-OfflineUnique @($modules.Snapshot.id) 'installed module ID'
        $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($name in $hostInfo.Names) { $expected.Add($name, (Get-OfflineEntryHash $hostSource $name)) }
        foreach ($module in $modules) {
            $prefix = "data/modules/packages/$($module.Snapshot.id)/$($module.Snapshot.version)/"
            foreach ($name in $module.Source.Entries.Keys) { $expected.Add($prefix + $name, (Get-OfflineEntryHash $module.Source $name)) }
        }
        $reused = $false
        if ([IO.File]::Exists($output)) {
            try {
                $bundle = Open-OfflineArchive $output
                Assert-OfflineBundleContent $bundle $expected $modules
                $reused = $true
            } catch {
                Close-OfflineArchive $bundle
                $bundle = $null
                if ($VerifyOnly -or -not $Force) { throw "Existing offline ZIP is not reusable; use another OutputPath or explicit Force. $($_.Exception.Message)" }
            }
        } elseif ($VerifyOnly) { throw "VerifyOnly requires an existing offline ZIP: $output" }
        if (-not $reused) {
            $package = Join-Path $work 'package'
            [IO.Directory]::CreateDirectory($package) | Out-Null
            foreach ($name in $hostInfo.Names) { Copy-OfflineEntry $hostSource $name (Join-Path $package $name) $package }
            $installed = @()
            foreach ($module in $modules) {
                $prefix = "data/modules/packages/$($module.Snapshot.id)/$($module.Snapshot.version)/"
                foreach ($name in $module.Source.Entries.Keys) { Copy-OfflineEntry $module.Source $name (Join-Path $package ($prefix + $name)) $package }
                $installed += @{ Id = $module.Snapshot.id; Version = $module.Snapshot.version; InstalledUtc = [DateTime]::UtcNow.ToString('O') }
            }
            $utf8 = [Text.UTF8Encoding]::new($false)
            [IO.File]::WriteAllText((Join-Path $package 'data/modules/installed.json'), (@{ SchemaVersion = 1; Modules = $installed } | ConvertTo-Json -Depth 10), $utf8)
            $notice = "完整离线包：解压整个 ZIP 后运行 GameValueEditor.exe。`n预装模块使用各自的运行时定位方式；名称识别与修改功能支持分开处理，不直接复用历史内存地址。`n未来查新和更新可能需要访问 GitHub。此包仅供本地或 QQ 分发，不是主程序在线更新资产。`n"
            if ($localOnly) { $notice = "本地测试版 / 未发布：包含未提交代码，不代表同版本正式发布资产，不可上传覆盖正式包。`n源码提交号仅为构建基线。请解压到新目录测试，勿覆盖正在使用的程序。`n" + $notice }
            [IO.File]::WriteAllText((Join-Path $package '完整离线包说明.txt'), $notice, $utf8)
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($temporary)) | Out-Null
            $archiveStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $temporaryCreated = $true
            try {
                Write-OfflineZip $archiveStream $package (@($expected.Keys) + @('data/modules/installed.json','完整离线包说明.txt'))
            } finally { $archiveStream.Dispose() }
            $bundle = Open-OfflineArchive $temporary
            Assert-OfflineBundleContent $bundle $expected $modules
        }
        Test-OfflineBundleModules $bundle (Join-Path $work 'modules') $ApplicationVersion $modules
        Test-OfflineBundleStartup $bundle (Join-Path $work 'startup')
        foreach ($source in @($hostSource) + @($modules | ForEach-Object { $_.Source })) {
            if ((Get-OfflineArchiveHash $source) -cne $source.Hash) { throw 'Source changed during offline packaging.' }
        }
        if ((Get-OfflineArchiveHash $bundle) -cne $bundle.Hash) { throw 'Offline ZIP changed during verification.' }
        $receiptPath = if (-not $VerifyOnly) { Save-OfflineReceipt $root $bundle $ApplicationVersion $hostInfo $hostSource.Hash $modules ($ModuleIds.Count -eq 0) -LocalOnly:$localOnly } else { $null }
        $freshness = if ($localOnly) { @{ IsLatest = $null; LatestStatus = 'LocalReview' } } else { Get-OfflineFreshness $moduleRoot $ApplicationVersion $hostInfo.Capability $modules ($ModuleIds.Count -eq 0) }
        $result = [pscustomobject]@{
            ArchivePath = $output; ApplicationVersion = $ApplicationVersion; SourceCommit = $hostInfo.SourceCommit
            HostSha256 = $hostSource.Hash; Sha256 = $bundle.Hash; SizeBytes = $bundle.Stream.Length
            Verified = $true; StartupVerified = $true; Reused = $reused; VerifyOnly = [bool]$VerifyOnly
            IntegrityVerified = $true; ModulesVerified = $true; IsLatest = $freshness.IsLatest; LatestStatus = $freshness.LatestStatus; ReceiptPath = $receiptPath
            LocalOnly = $localOnly
            Modules = @($modules | ForEach-Object { [pscustomobject]@{ Id = $_.Snapshot.id; Version = $_.Snapshot.version; Sha256 = $_.Source.Hash } })
        }
        Close-OfflineArchive $bundle
        $bundle = $null
        if (-not $reused) {
            $null = Assert-OfflinePath $output $dist
            [IO.File]::Move($temporary, $output, [bool]$Force)
            $temporaryCreated = $false
        }
        return $result
    } finally {
        Close-OfflineArchive $bundle
        Close-OfflineArchive $hostSource
        foreach ($module in $modules) { Close-OfflineArchive $module.Source }
        if ($temporaryCreated -and [IO.File]::Exists($temporary)) {
            $null = Assert-OfflinePath $temporary $dist
            Remove-Item -LiteralPath $temporary -Force
        }
        if ($workCreated -and [IO.Directory]::Exists($work)) {
            $null = Assert-OfflinePath $work $artifacts
            Remove-Item -LiteralPath $work -Recurse -Force
        }
    }
}
