# BitBroom publish script — produces self-contained single-file executables.
# Usage: .\build\publish.ps1 [-Runtime win-x64] [-Configuration Release]

param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\$Runtime"

Write-Host "Publishing BitBroom ($Configuration, $Runtime)…" -ForegroundColor Cyan
# Start clean so stale files from a previous local run never end up in the portable zip.
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# GUI — WPF cannot be trimmed; compression keeps the single file reasonable.
dotnet publish (Join-Path $root "src\BitBroom.App\BitBroom.App.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed" }

# CLI
dotnet publish (Join-Path $root "src\BitBroom.Cli\BitBroom.Cli.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

Copy-Item (Join-Path $root "LICENSE") $dist -Force
Copy-Item (Join-Path $root "README.md") $dist -Force -ErrorAction SilentlyContinue

Get-ChildItem $dist -File | ForEach-Object {
    Write-Host ("  {0,-22} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB))
}

Write-Host "`nDone → $dist" -ForegroundColor Green
