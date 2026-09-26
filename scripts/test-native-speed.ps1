[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourcePath = Join-Path $repoRoot "tests\NativeSpeedTarget\NativeSpeedTarget.cpp"
$definitionPath = Join-Path $repoRoot "tests\NativeSpeedTarget\kernel32.def"
$outputDirectory = Join-Path $repoRoot "artifacts\native-speed-target"
$targetPath = Join-Path $outputDirectory "NativeSpeedTarget.exe"
$objectPath = Join-Path $outputDirectory "NativeSpeedTarget.obj"
$importLibraryPath = Join-Path $outputDirectory "kernel32.lib"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) { throw "Visual Studio vswhere.exe was not found." }
$installationPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($installationPath)) { throw "Visual Studio C++ x64 tools were not found." }
$developerShell = Join-Path $installationPath "Common7\Tools\VsDevCmd.bat"
if (-not (Test-Path -LiteralPath $developerShell)) { throw "Visual Studio developer shell was not found." }

$compileCommand = '"{0}" -no_logo -arch=x64 -host_arch=x64 && lib.exe /nologo /def:"{1}" /machine:x64 /out:"{2}" && cl.exe /nologo /c /O2 /GS- /GR- /EHsc- /Zl "{3}" /Fo:"{4}" && link.exe /nologo /machine:x64 /subsystem:console /entry:mainCRTStartup /nodefaultlib /manifest:no "{4}" "{2}" /out:"{5}"' -f `
    $developerShell, $definitionPath, $importLibraryPath, $sourcePath, $objectPath, $targetPath
& cmd.exe /d /s /c $compileCommand
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $targetPath)) {
    throw "Native x64 speed target compilation failed."
}

$previousTarget = $env:GVE_NATIVE_SPEED_TARGET
try {
    $env:GVE_NATIVE_SPEED_TARGET = $targetPath
    dotnet run --project (Join-Path $repoRoot "tests\GameValueEditor.SmokeTests\GameValueEditor.SmokeTests.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Native speed smoke test failed." }
}
finally {
    $env:GVE_NATIVE_SPEED_TARGET = $previousTarget
}
