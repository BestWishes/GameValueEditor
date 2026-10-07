# Loaded by offline-bundle.ps1. Verification receipts are local evidence, not signatures.
function Assert-OfflineModuleVerifierReport {
    param($Report, [string]$Version, $Target, $Modules)
    $caps = $Target.Compatibility
    if (-not (Test-OfflineInteger $Target.SchemaVersion) -or $Target.SchemaVersion -ne 1 -or
        $Target.Version -cne $Version -or -not (Test-OfflineInteger $caps.MinimumModuleHostApi) -or
        -not (Test-OfflineInteger $caps.MaximumModuleHostApi) -or $caps.MinimumModuleHostApi -lt 1 -or
        $caps.MaximumModuleHostApi -lt $caps.MinimumModuleHostApi) {
        throw 'Invalid target host compatibility for module page verification.'
    }
    if (-not (Test-OfflineInteger $Report.SchemaVersion) -or $Report.SchemaVersion -ne 1 -or
        $Report.ApplicationVersion -cne $Version -or -not (Test-OfflineInteger $Report.HostApiVersion) -or
        $Report.HostApiVersion -ne $caps.MaximumModuleHostApi -or $Report.Verified -isnot [bool] -or -not $Report.Verified) {
        throw 'Module page verifier must match the target host version and API.'
    }
    $expected = @($Modules | ForEach-Object { @{ Id = $_.Snapshot.id; Version = $_.Snapshot.version; EditorIds = @($_.Snapshot.editors.id) } })
    Assert-OfflineEqual @($Report.Modules | Sort-Object Id) @($expected | Sort-Object Id) 'actual loaded modules/pages'
}

function Test-OfflineBundleModules {
    param($Bundle, [string]$Directory, [string]$Version, $Modules)
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    foreach ($name in $Bundle.Entries.Keys) { Copy-OfflineEntry $Bundle $name (Join-Path $Directory $name) $Directory }
    $verifier = Join-Path $PSScriptRoot '../tests/GameValueEditor.SmokeTests/bin/Release/net8.0-windows/GameValueEditor.SmokeTests.exe'
    if (-not [IO.File]::Exists($verifier)) { throw 'Module page verifier is missing. Run dotnet build GameValueEditor.sln -c Release first.' }
    $reportPath = Join-Path $Directory 'module-verification.json'
    $errorPath = Join-Path $Directory 'module-verifier-error.txt'
    $process = Start-Process -FilePath $verifier -WorkingDirectory $Directory -WindowStyle Hidden -PassThru `
        -RedirectStandardError $errorPath -RedirectStandardOutput (Join-Path $Directory 'module-verifier-output.txt') `
        -ArgumentList @('"--verify-offline-directory=' + $Directory + '"', '"--module-verification-report=' + $reportPath + '"')
    try {
        if (-not $process.WaitForExit(30000)) { throw 'Module page verifier timed out.' }
        if ($process.ExitCode -ne 0) {
            $detail = 'No bounded verifier error detail was returned.'
            if ([IO.File]::Exists($errorPath) -and (Get-Item -LiteralPath $errorPath).Length -le 4MB) {
                $detail = (([IO.File]::ReadAllText($errorPath, [Text.Encoding]::UTF8)) -split '\r?\n')[0]
            }
            if ($detail.Length -gt 600) { $detail = $detail.Substring(0,600) }
            $detail = $detail.Replace($Directory, '[isolated]')
            throw "Actual module load/page verification failed (exit $($process.ExitCode)): $detail"
        }
        $report = (Read-OfflineJsonFile $reportPath) | ConvertFrom-Json -AsHashtable
        $target = (Read-OfflineEntryText $Bundle 'release-compatibility.json') | ConvertFrom-Json -AsHashtable
        Assert-OfflineModuleVerifierReport $report $Version $target $Modules
    } finally {
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
        $process.Dispose()
    }
}

function Get-OfflineReceiptPath {
    param([string]$Root, [string]$Hash)
    if ($Hash -cnotmatch '^[0-9A-F]{64}$') { throw 'Invalid receipt hash.' }
    return Assert-OfflinePath (Join-Path $Root "artifacts/offline-bundle-receipts/$Hash.json") (Join-Path $Root 'artifacts')
}

function Save-OfflineReceipt {
    param([string]$Root, $Bundle, [string]$Version, $HostInfo, [string]$HostHash, $Modules, [bool]$Complete)
    $path = Get-OfflineReceiptPath $Root $Bundle.Hash
    $files = @{}
    foreach ($name in $Bundle.Entries.Keys) { $files[$name] = Get-OfflineEntryHash $Bundle $name }
    $receipt = @{ SchemaVersion = 1; Sha256 = $Bundle.Hash; SizeBytes = $Bundle.Stream.Length; ApplicationVersion = $Version
        SourceCommit = $HostInfo.SourceCommit; Capability = $HostInfo.Capability; HostSha256 = $HostHash; Complete = $Complete
        Files = $files; Modules = @($Modules | ForEach-Object { @{ Snapshot = $_.Snapshot; Sha256 = $_.Source.Hash } }) }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    # Existing evidence is never silently repaired or replaced by verification.
    if ([IO.File]::Exists($path)) {
        $existing = (Read-OfflineJsonFile $path) | ConvertFrom-Json -AsHashtable
        # A different invocation can reuse exactly the same bytes. Keep the original recipe intent.
        $receipt.Complete = $existing.Complete
        Assert-OfflineEqual $existing $receipt 'existing local receipt'
        return $path
    }
    $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $path)
    } finally { if ([IO.File]::Exists($temporary)) { Remove-Item -LiteralPath $temporary -Force } }
    return $path
}

function Get-OfflineFreshness {
    param([string]$ModuleRoot, [string]$Version, $Capability, $Modules, [bool]$Complete)
    try {
        $catalogJson = Read-OfflineJsonFile (Assert-OfflinePath (Join-Path $ModuleRoot 'catalog.json') $ModuleRoot)
        Assert-OfflineJsonSchema $catalogJson (Join-Path $ModuleRoot 'schemas/catalog.schema.json')
        $catalog = $catalogJson | ConvertFrom-Json -AsHashtable
        $latest = @(Select-OfflineModules $catalog $Capability $Version @($Modules.Snapshot.id))
        $upToDate = $true
        foreach ($module in $Modules) {
            $current = @($latest | Where-Object { $_.id -ceq $module.Snapshot.id })[0]
            $originalHash = if ($module.Source) { $module.Source.Hash } else { $module.Sha256 }
            if ($current.version -ceq $module.Snapshot.version -and $current.sha256 -cne $originalHash) {
                throw 'Catalog changed an immutable module asset without changing its version.'
            }
            if ($current.version -cne $module.Snapshot.version) { $upToDate = $false }
        }
        if ($Complete -and @($catalog.modules).Count -ne @($Modules).Count) { $upToDate = $false }
        return @{ IsLatest = $upToDate; LatestStatus = $(if ($upToDate) { 'Current' } else { 'NewerRecipeAvailable' }) }
    } catch { return @{ IsLatest = $null; LatestStatus = 'Unknown' } }
}

function Test-OfflineReceiptBundle {
    param([string]$Root, [string]$ModuleRoot, [string]$Output, [string]$Version, [string[]]$ModuleIds)
    $bundle = $null
    $work = Assert-OfflinePath (Join-Path $Root ('artifacts/offline-verify-' + [guid]::NewGuid().ToString('N'))) (Join-Path $Root 'artifacts')
    try {
        $bundle = Open-OfflineArchive $Output
        $receiptPath = Get-OfflineReceiptPath $Root $bundle.Hash
        if (-not [IO.File]::Exists($receiptPath)) { return $null }
        $receipt = (Read-OfflineJsonFile $receiptPath) | ConvertFrom-Json -AsHashtable
        if ($receipt.SchemaVersion -ne 1 -or $receipt.Sha256 -cne $bundle.Hash -or $receipt.SizeBytes -ne $bundle.Stream.Length -or
            $receipt.ApplicationVersion -cne $Version -or $receipt.SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
            $receipt.Files -isnot [Collections.IDictionary] -or $receipt.Modules -isnot [array]) { throw 'Invalid local verification receipt.' }
        Assert-OfflineEntries $bundle @($receipt.Files.Keys)
        foreach ($name in $receipt.Files.Keys) {
            if ((Get-OfflineEntryHash $bundle $name) -cne $receipt.Files[$name]) { throw "Receipt file hash mismatch: $name" }
        }
        $expected = @{} + $receipt.Files
        $expected.Remove('data/modules/installed.json'); $expected.Remove('完整离线包说明.txt')
        Assert-OfflineBundleContent $bundle $expected $receipt.Modules
        if ($ModuleIds.Count -gt 0) { Assert-OfflineEqual @($receipt.Modules.Snapshot.id | Sort-Object) @($ModuleIds | Sort-Object) 'requested installed module IDs' }
        $capability = (Read-OfflineEntryText $bundle 'release-compatibility.json') | ConvertFrom-Json -AsHashtable
        Assert-OfflineEqual $capability.Compatibility $receipt.Capability 'frozen host capability'
        if ($capability.SchemaVersion -ne 1 -or $capability.Version -cne $Version) { throw 'Frozen host version mismatch.' }
        [IO.Directory]::CreateDirectory($work) | Out-Null
        foreach ($name in @('GameValueEditor.exe','GameValueEditor.Updater.exe')) {
            $binary = Join-Path $work $name
            Copy-OfflineEntry $bundle $name $binary $work
            if ((Get-OfflineBinaryProductVersion $binary) -cne "$Version+$($receipt.SourceCommit)") { throw 'Frozen binary ProductVersion mismatch.' }
        }
        Test-OfflineBundleModules $bundle (Join-Path $work 'modules') $Version $receipt.Modules
        Test-OfflineBundleStartup $bundle (Join-Path $work 'startup')
        $freshness = Get-OfflineFreshness $ModuleRoot $Version $receipt.Capability $receipt.Modules $receipt.Complete
        return [pscustomobject]@{ ArchivePath = $Output; ApplicationVersion = $Version; SourceCommit = $receipt.SourceCommit
            HostSha256 = $receipt.HostSha256; Sha256 = $bundle.Hash; SizeBytes = $bundle.Stream.Length
            Verified = $true; IntegrityVerified = $true; ModulesVerified = $true; StartupVerified = $true
            VerifyOnly = $true; Reused = $true; IsLatest = $freshness.IsLatest; LatestStatus = $freshness.LatestStatus; ReceiptPath = $receiptPath
            Modules = @($receipt.Modules | ForEach-Object { [pscustomobject]@{ Id = $_.Snapshot.id; Version = $_.Snapshot.version; Sha256 = $_.Sha256 } }) }
    } finally {
        Close-OfflineArchive $bundle
        if ([IO.Directory]::Exists($work)) {
            $null = Assert-OfflinePath $work (Join-Path $Root 'artifacts')
            Remove-Item -LiteralPath $work -Recurse -Force
        }
    }
}

function Select-OfflineInstalledSnapshots {
    param($Catalog, $Capability, [string]$Version, $Installed)
    $frozen = @{ schemaVersion = $Catalog.schemaVersion; hostApiVersion = $Catalog.hostApiVersion; modules = @() }
    foreach ($record in $Installed.Modules) {
        $entry = @($Catalog.modules | Where-Object { $_.id -ceq $record.Id })
        $release = @($entry.releases | Where-Object { $_.version -ceq $record.Version })
        if ($entry.Count -ne 1 -or $release.Count -ne 1) { throw "Missing verification evidence for installed $($record.Id) v$($record.Version); restore its receipt or original release inputs." }
        $snapshot = @{} + $entry[0]
        foreach ($field in @('maximumHostVersion','supportsUnlistedBuildValidation')) { $snapshot.Remove($field) }
        foreach ($key in $release[0].Keys) { $snapshot[$key] = $release[0][$key] }
        $snapshot.releases = @($release[0])
        $frozen.modules += $snapshot
    }
    return @(Select-OfflineModules $frozen $Capability $Version @())
}
