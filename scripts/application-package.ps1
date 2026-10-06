# Shared by the guarded release entry and local review builds. Never pre-delete packages.
. (Join-Path $PSScriptRoot 'offline-bundle.ps1')

function Build-ApplicationBinaries {
    param([string]$Root, [string]$Work)
    foreach ($project in @('GameValueEditor', 'GameValueEditor.Updater')) {
        & dotnet publish (Join-Path $Root "src/$project/$project.csproj") -c Release -r win-x64 `
            --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
            -o (Join-Path $Work $project) | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
    }
}

function Test-ApplicationCandidate {
    param($Archive, [string]$Version, [string]$Commit, [string]$Work)
    $info = Assert-OfflineHost $Archive $Version (Join-Path $Work 'binary-check') $null $Archive.Hash
    if ($info.SourceCommit -cne $Commit) { throw 'Candidate binaries are not traceable to the requested source commit.' }
    Test-OfflineBundleStartup $Archive (Join-Path $Work 'startup')
    & (Join-Path $PSScriptRoot 'test-updater.ps1') -UpdaterPath (Join-Path $Work 'startup/GameValueEditor.Updater.exe') | Out-Host
}

function Invoke-ApplicationPackage {
    param([string]$Root, [string]$Version, [string]$Commit, [string]$OutputPath)
    $ErrorActionPreference = 'Stop'
    $null = Assert-OfflineVersion $Version
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid source commit.' }
    $rootPath = [IO.Path]::GetFullPath($Root)
    $output = Assert-OfflinePath $OutputPath $rootPath
    if ([IO.File]::Exists($output)) { throw 'An existing package is immutable; choose a new version or local review directory.' }
    $artifacts = Join-Path $rootPath 'artifacts'
    $work = Assert-OfflinePath (Join-Path $artifacts ('application-build-' + [guid]::NewGuid().ToString('N'))) $artifacts
    $temporary = Assert-OfflinePath (Join-Path ([IO.Path]::GetDirectoryName($output)) ('.application-' + [guid]::NewGuid().ToString('N') + '.zip')) $rootPath
    $created = $false
    $archive = $null
    try {
        [IO.Directory]::CreateDirectory($work) | Out-Null
        Build-ApplicationBinaries $rootPath $work
        $package = Join-Path $work 'package'
        [IO.Directory]::CreateDirectory($package) | Out-Null
        foreach ($project in @('GameValueEditor','GameValueEditor.Updater')) {
            Copy-Item -LiteralPath (Join-Path $work "$project/$project.exe") -Destination $package
        }
        foreach ($name in @('README.md','LICENSE','SECURITY.md')) { Copy-Item -LiteralPath (Join-Path $rootPath $name) -Destination $package }
        [IO.File]::WriteAllText((Join-Path $package 'release-compatibility.json'), (@{
            SchemaVersion = 1; Version = $Version; Compatibility = @{
                MinimumModuleHostApi = 2; MaximumModuleHostApi = 7; MaximumCatalogSchemaVersion = 5
            }
        } | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($temporary)) | Out-Null
        $stream = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
        $created = $true
        try { Write-OfflineZip $stream $package @('GameValueEditor.exe','GameValueEditor.Updater.exe','README.md','LICENSE','SECURITY.md','release-compatibility.json') }
        finally { $stream.Dispose() }
        $archive = Open-OfflineArchive $temporary
        Test-ApplicationCandidate $archive $Version $Commit $work
        $hash = Get-OfflineArchiveHash $archive
        if ($hash -cne $archive.Hash) { throw 'Candidate ZIP changed during verification.' }
        Close-OfflineArchive $archive
        $archive = $null
        $null = Assert-OfflinePath $output $rootPath
        [IO.File]::Move($temporary, $output) # No overwrite; promotion is on the same volume.
        $created = $false
        [pscustomobject]@{ ArchivePath = $output; Sha256 = $hash; SourceCommit = $Commit; Verified = $true }
    } finally {
        Close-OfflineArchive $archive
        if ($created -and [IO.File]::Exists($temporary)) {
            $null = Assert-OfflinePath $temporary $rootPath
            Remove-Item -LiteralPath $temporary -Force
        }
        if ([IO.Directory]::Exists($work)) {
            $null = Assert-OfflinePath $work $artifacts
            Remove-Item -LiteralPath $work -Recurse -Force
        }
    }
}
