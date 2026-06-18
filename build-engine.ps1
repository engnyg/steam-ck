#!/usr/bin/env pwsh
# Builds the OpenSteamTool native engine and copies the Release DLLs into the
# WPF app's engine/ folder so EngineService can deploy them.
#
# Requires: CMake 3.20+, Visual Studio 2022 (VC++ tools) or Ninja.

$ErrorActionPreference = 'Stop'

$repoRoot   = $PSScriptRoot
$ostDir     = Join-Path $repoRoot 'external/OpenSteamTool'
$engineDest = Join-Path $repoRoot 'src/OpenSteamUnlocker.App/engine'
$config     = 'Release'

if (-not (Test-Path (Join-Path $ostDir 'build.bat'))) {
    throw "OpenSteamTool submodule not found at $ostDir. Run: git submodule update --init --recursive"
}

Write-Host "[INFO] Building OpenSteamTool ($config)..." -ForegroundColor Cyan
Push-Location $ostDir
try {
    $env:CONFIGS = $config
    & cmd.exe /c (Join-Path $ostDir 'build.bat')
    if ($LASTEXITCODE -ne 0) { throw "OpenSteamTool build failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

# Locate the produced DLLs (Ninja Multi-Config and VS layouts differ).
$dllNames = @('OpenSteamTool.dll', 'dwmapi.dll', 'xinput1_4.dll')
New-Item -ItemType Directory -Force $engineDest | Out-Null

foreach ($name in $dllNames) {
    $found = Get-ChildItem -Path (Join-Path $ostDir 'build') -Recurse -Filter $name -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match "(?i)\\$config\\" -or $_.FullName -notmatch '(?i)\\(Debug)\\' } |
             Select-Object -First 1
    if (-not $found) { throw "Built DLL not found: $name" }
    Copy-Item $found.FullName (Join-Path $engineDest $name) -Force
    Write-Host "[OK] $name -> engine/" -ForegroundColor Green
}

Write-Host "[DONE] Engine binaries copied to $engineDest" -ForegroundColor Cyan
