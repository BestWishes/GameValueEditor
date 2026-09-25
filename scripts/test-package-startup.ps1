[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath
)

$ErrorActionPreference = "Stop"
$resolvedArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$smokeDirectory = Join-Path $repoRoot ("artifacts\startup-smoke-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Path $smokeDirectory | Out-Null
Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $smokeDirectory

$executable = Join-Path $smokeDirectory "GameValueEditor.exe"
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru

try {
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) {
        throw "Packaged application exited during startup with code $($process.ExitCode)."
    }

    Write-Host "Startup smoke test passed."
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
    }
}
