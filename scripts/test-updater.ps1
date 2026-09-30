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
    dotnet build $project -c Release | Out-Host
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

function Start-Updater([string]$resolvedUpdater, $fixture) {
    return Start-Process -FilePath $resolvedUpdater -WindowStyle Hidden -PassThru -ArgumentList @(
        "--pending", $fixture.PendingPath, "--pid", "2147483647", "--app-dir", $fixture.AppDirectory)
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
        $transientProcess = Start-Updater $resolvedUpdater $transient
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

    Write-Host "Updater sandbox smoke tests passed: temporary lock retry and persistent lock rollback."
}
finally {
    if ($sandboxRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $sandboxRoot)) {
        Remove-Item -LiteralPath $sandboxRoot -Recurse -Force
    }
}
