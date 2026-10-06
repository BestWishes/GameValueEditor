[CmdletBinding()]
param(
    [string]$UpdaterPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sandboxRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ("artifacts\updater-smoke-" + [Guid]::NewGuid().ToString("N"))))
if (-not $sandboxRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Updater smoke path escaped the repository."
}

function Resolve-Updater {
    if (-not [string]::IsNullOrWhiteSpace($UpdaterPath)) {
        return (Resolve-Path -LiteralPath $UpdaterPath).Path
    }

    $project = Join-Path $repoRoot "src\GameValueEditor.Updater\GameValueEditor.Updater.csproj"
    dotnet build $project -c Release -p:Platform=x64 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Updater build failed with code $LASTEXITCODE." }
    return (Resolve-Path -LiteralPath (Join-Path $repoRoot "src\GameValueEditor.Updater\bin\x64\Release\net8.0-windows\GameValueEditor.Updater.exe")).Path
}

function New-UpdateFixture([string]$name, [bool]$includeLockedFile) {
    $root = Join-Path $sandboxRoot $name
    $appDirectory = Join-Path $root "app"
    $updatesDirectory = Join-Path $appDirectory "data\updates"
    $sourceDirectory = Join-Path $root "source"
    $archivePath = Join-Path $updatesDirectory "update.zip"
    $pendingPath = Join-Path $updatesDirectory "pending-update.json"

    New-Item -ItemType Directory -Path $appDirectory, $updatesDirectory, $sourceDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $appDirectory "GameValueEditor.exe") -Value "old-app" -Encoding utf8NoBOM
    New-Item -ItemType Directory -Path (Join-Path $appDirectory "data") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $appDirectory "data\library.json") -Value "preserve-me" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $sourceDirectory "GameValueEditor.exe") -Value "new-app" -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $sourceDirectory "README.md") -Value "new-readme" -Encoding utf8NoBOM
    if ($includeLockedFile) {
        Set-Content -LiteralPath (Join-Path $appDirectory "zz-locked.dll") -Value "old-locked" -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $sourceDirectory "zz-locked.dll") -Value "new-locked" -Encoding utf8NoBOM
    }
    Compress-Archive -Path (Join-Path $sourceDirectory "*") -DestinationPath $archivePath

    $pending = [ordered]@{
        Version = "99.0.0"
        ArchivePath = $archivePath
        Sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        DownloadedUtc = [DateTime]::UtcNow
    }
    $pending | ConvertTo-Json | Set-Content -LiteralPath $pendingPath -Encoding utf8NoBOM
    return [pscustomobject]@{
        Root = $root
        AppDirectory = $appDirectory
        UpdatesDirectory = $updatesDirectory
        ArchivePath = $archivePath
        PendingPath = $pendingPath
    }
}

function Start-Updater([string]$resolvedUpdater, $fixture, [bool]$appendDirectorySeparator = $false) {
    $appDirectoryArgument = $fixture.AppDirectory
    if ($appendDirectorySeparator -and -not [System.IO.Path]::EndsInDirectorySeparator($appDirectoryArgument)) {
        $appDirectoryArgument += [System.IO.Path]::DirectorySeparatorChar
    }
    return Start-Process -FilePath $resolvedUpdater -WindowStyle Hidden -PassThru -ArgumentList @(
        "--pending", $fixture.PendingPath, "--pid", "2147483647", "--app-dir", $appDirectoryArgument)
}

function Wait-Updater($process, [int]$timeoutMilliseconds = 30000) {
    if (-not $process.WaitForExit($timeoutMilliseconds)) {
        try { $process.Kill($true) } catch { }
        throw "Updater did not exit within $timeoutMilliseconds ms."
    }
    return $process.ExitCode
}

New-Item -ItemType Directory -Path $sandboxRoot -Force | Out-Null
try {
    $resolvedUpdater = Resolve-Updater

    $transient = New-UpdateFixture "transient-lock" $false
    $applicationPath = Join-Path $transient.AppDirectory "GameValueEditor.exe"
    $transientLock = [System.IO.File]::Open($applicationPath, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        # AppContext.BaseDirectory ends with a directory separator in the real application.
        $transientProcess = Start-Updater $resolvedUpdater $transient $true
        Start-Sleep -Milliseconds 900
        if ($transientProcess.HasExited) { throw "Updater did not retry a temporarily locked application file." }
    }
    finally {
        $transientLock.Dispose()
    }
    $transientExit = Wait-Updater $transientProcess
    if ($transientExit -ne 0) { throw "Updater failed after a temporary lock was released (code $transientExit)." }
    if ((Get-Content -LiteralPath $applicationPath -Raw).Trim() -ne "new-app") {
        throw "Updater did not replace application files after the temporary lock was released."
    }
    if ((Get-Content -LiteralPath (Join-Path $transient.AppDirectory "data\library.json") -Raw).Trim() -ne "preserve-me") {
        throw "Updater overwrote portable user data."
    }
    if (Test-Path -LiteralPath $transient.PendingPath) { throw "Updater did not clear the pending manifest." }

    $persistent = New-UpdateFixture "persistent-lock" $true
    $lockedPath = Join-Path $persistent.AppDirectory "zz-locked.dll"
    $persistentLock = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $persistentProcess = Start-Updater $resolvedUpdater $persistent
        $persistentExit = Wait-Updater $persistentProcess 30000
    }
    finally {
        $persistentLock.Dispose()
    }
    if ($persistentExit -eq 0) { throw "Updater unexpectedly succeeded while a package file remained locked." }
    if ((Get-Content -LiteralPath (Join-Path $persistent.AppDirectory "GameValueEditor.exe") -Raw).Trim() -ne "old-app") {
        throw "Updater did not roll back files replaced before a persistent lock failure."
    }
    if ((Get-Content -LiteralPath $lockedPath -Raw).Trim() -ne "old-locked") {
        throw "Updater changed the persistently locked file."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $persistent.UpdatesDirectory "last-update-error.json"))) {
        throw "Updater did not create a one-time failure notice."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $persistent.UpdatesDirectory "update-error.log"))) {
        throw "Updater did not preserve its failure diagnostic."
    }
    $transactionResidue = @(Get-ChildItem -LiteralPath $persistent.AppDirectory -Directory -Filter ".update-transaction-*" -ErrorAction Stop)
    if ($transactionResidue.Count -ne 0) { throw "Updater left transaction directories after rollback." }

    $recovery = New-UpdateFixture "recovery-lock" $true
    $recoveryExe = Join-Path $recovery.AppDirectory "GameValueEditor.exe"
    $failureLock = [IO.File]::Open((Join-Path $recovery.AppDirectory "zz-locked.dll"),
        [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $restoreLock = $null
    try {
        $recoveryProcess = Start-Updater $resolvedUpdater $recovery
        $deadline = [DateTime]::UtcNow.AddSeconds(6)
        $replaced = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            try { $replaced = (Get-Content -LiteralPath $recoveryExe -Raw).Trim() -eq 'new-app' } catch { }
            if ($replaced) { break }
            Start-Sleep -Milliseconds 20
        }
        if (-not $replaced) { throw 'Recovery fixture did not reach the first replaced file.' }
        $restoreLock = [IO.File]::Open($recoveryExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ((Wait-Updater $recoveryProcess 40000) -eq 0) { throw 'Recovery lock unexpectedly succeeded.' }
        $transactions = @(Get-ChildItem -LiteralPath $recovery.AppDirectory -Directory -Filter '.update-transaction-*')
        if ($transactions.Count -ne 1) { throw 'Failed recovery did not preserve its transaction directory.' }
        $journalPath = Join-Path $transactions[0].FullName 'recovery.json'
        $markerPath = Join-Path $recovery.UpdatesDirectory 'recovery-required.json'
        $backupExe = Join-Path $transactions[0].FullName 'backup\GameValueEditor.exe'
        if (-not (Test-Path -LiteralPath $journalPath) -or -not (Test-Path -LiteralPath $markerPath) -or
            (Get-Content -LiteralPath $backupExe -Raw).Trim() -ne 'old-app') {
            throw 'Failed recovery lost its journal, marker or original application backup.'
        }
        $retry = Start-Process -FilePath $resolvedUpdater -WindowStyle Hidden -PassThru -ArgumentList @(
            '--recover', $journalPath, '--app-dir', $recovery.AppDirectory)
        if ((Wait-Updater $retry 20000) -eq 0 -or -not (Test-Path -LiteralPath $backupExe)) {
            throw 'Repeated locked recovery did not preserve the original backup.'
        }
    }
    finally {
        if ($restoreLock) { $restoreLock.Dispose() }
        $failureLock.Dispose()
    }
    $restore = Start-Process -FilePath $resolvedUpdater -WindowStyle Hidden -PassThru -ArgumentList @(
        '--recover', $journalPath, '--app-dir', $recovery.AppDirectory)
    if ((Wait-Updater $restore 20000) -ne 0 -or
        (Get-Content -LiteralPath $recoveryExe -Raw).Trim() -ne 'old-app' -or
        (Test-Path -LiteralPath $markerPath) -or (Test-Path -LiteralPath $journalPath) -or
        (Get-Content -LiteralPath (Join-Path $recovery.AppDirectory 'data\library.json') -Raw).Trim() -ne 'preserve-me') {
        throw 'Explicit recovery did not restore old files, clear recovery state and preserve user data.'
    }

    foreach ($unsafePath in @('../outside.txt', 'data/library.json', 'C:/outside.txt')) {
        $invalid = New-UpdateFixture ("invalid-" + [Guid]::NewGuid().ToString('N')) $false
        $invalidTransaction = Join-Path $invalid.AppDirectory '.update-transaction-invalid'
        $invalidBackup = Join-Path $invalidTransaction 'backup'
        New-Item -ItemType Directory -Path $invalidBackup -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $invalidBackup 'GameValueEditor.exe') -Value 'backup-app' -Encoding utf8NoBOM
        $invalidJournal = Join-Path $invalidTransaction 'recovery.json'
        @{ ApplicationDirectory = $invalid.AppDirectory; Files = @(
            @{ RelativePath = 'GameValueEditor.exe'; HadOriginal = $true },
            @{ RelativePath = $unsafePath; HadOriginal = $false }) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $invalidJournal -Encoding utf8NoBOM
        $invalidRestore = Start-Process -FilePath $resolvedUpdater -WindowStyle Hidden -PassThru -ArgumentList @(
            '--recover', $invalidJournal, '--app-dir', $invalid.AppDirectory)
        if ((Wait-Updater $invalidRestore) -eq 0 -or
            (Get-Content -LiteralPath (Join-Path $invalid.AppDirectory 'GameValueEditor.exe') -Raw).Trim() -ne 'old-app' -or
            (Get-Content -LiteralPath (Join-Path $invalid.AppDirectory 'data/library.json') -Raw).Trim() -ne 'preserve-me' -or
            -not (Test-Path -LiteralPath $invalidJournal)) {
            throw 'Unsafe recovery journal changed files or deleted recovery evidence.'
        }
    }
    Write-Host "Updater sandbox tests passed: temporary lock, rollback, failed recovery backup preservation, repeat failure, explicit recovery and unsafe journal rejection."
}
finally {
    if ($sandboxRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $sandboxRoot)) {
        Remove-Item -LiteralPath $sandboxRoot -Recurse -Force
    }
}
