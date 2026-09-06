$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $root 'releases'
$package = Get-ChildItem -LiteralPath $releaseRoot -Directory -Filter 'Prism-*-win-x64-*' -ErrorAction SilentlyContinue |
    Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'DesktopAgent.exe')) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json')) } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $package) { Write-Host 'No portable build found. Run scripts/Build.ps1 -Task Package first.'; exit 1 }
Start-Process -FilePath (Join-Path $package.FullName 'DesktopAgent.exe') -WorkingDirectory $package.FullName
