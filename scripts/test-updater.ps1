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

$appDirectory = Join-Path $sandboxRoot "app"
$updatesDirectory = Join-Path $appDirectory "data\updates"
$sourceDirectory = Join-Path $sandboxRoot "source"
$archivePath = Join-Path $updatesDirectory "update.zip"
$pendingPath = Join-Path $updatesDirectory "pending-update.json"

New-Item -ItemType Directory -Path $appDirectory, $updatesDirectory, $sourceDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path $appDirectory "GameValueEditor.exe") -Value "old-app" -Encoding utf8NoBOM
New-Item -ItemType Directory -Path (Join-Path $appDirectory "data") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $appDirectory "data\library.json") -Value "preserve-me" -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $sourceDirectory "GameValueEditor.exe") -Value "new-app" -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $sourceDirectory "README.md") -Value "new-readme" -Encoding utf8NoBOM
Compress-Archive -Path (Join-Path $sourceDirectory "*") -DestinationPath $archivePath

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$pending = [ordered]@{
    Version = "99.0.0"
    ArchivePath = $archivePath
    Sha256 = $hash
    DownloadedUtc = [DateTime]::UtcNow
}
$pending | ConvertTo-Json | Set-Content -LiteralPath $pendingPath -Encoding utf8NoBOM

try {
    if ([string]::IsNullOrWhiteSpace($UpdaterPath)) {
        dotnet run --project (Join-Path $repoRoot "src\GameValueEditor.Updater\GameValueEditor.Updater.csproj") -c Release -- `
            --pending $pendingPath --pid 2147483647 --app-dir $appDirectory
        if ($LASTEXITCODE -ne 0) { throw "Updater exited with code $LASTEXITCODE." }
    }
    else {
        $resolvedUpdater = (Resolve-Path -LiteralPath $UpdaterPath).Path
        $updaterProcess = Start-Process -FilePath $resolvedUpdater -WindowStyle Hidden -PassThru -Wait -ArgumentList @(
            "--pending", $pendingPath, "--pid", "2147483647", "--app-dir", $appDirectory)
        if ($updaterProcess.ExitCode -ne 0) { throw "Packaged updater exited with code $($updaterProcess.ExitCode)." }
    }
    if ((Get-Content -LiteralPath (Join-Path $appDirectory "GameValueEditor.exe") -Raw).Trim() -ne "new-app") {
        throw "Updater did not replace application files."
    }
    if ((Get-Content -LiteralPath (Join-Path $appDirectory "data\library.json") -Raw).Trim() -ne "preserve-me") {
        throw "Updater overwrote portable user data."
    }
    if (Test-Path -LiteralPath $pendingPath) { throw "Updater did not clear the pending manifest." }
    Write-Host "Updater sandbox smoke test passed."
}
finally {
    if ($sandboxRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $sandboxRoot)) {
        Remove-Item -LiteralPath $sandboxRoot -Recurse -Force
    }
}
