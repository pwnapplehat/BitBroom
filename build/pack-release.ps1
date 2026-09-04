# Pack GitHub Release assets from already-published dist\win-* folders.
# Run after ./build/publish.ps1 (both runtimes) and with ISCC on PATH.
#
# Writes into release\ (gitignored):
#   BitBroom-<ver>-portable-win-x64.zip
#   BitBroom-<ver>-portable-win-arm64.zip
#   BitBroom-<ver>-setup.exe
#   SHA256SUMS.txt          (GNU text mode: "<hash>  <name>" — two spaces; matches
#                            UpdateService.cs which does not strip a leading '*')
#
# On a v* tag, the Directory.Build.props Version MUST equal the tag (v1.2.5 ↔ 1.2.5).
# That stops a forgotten version bump from shipping a setup.exe named for the previous
# release, which would also break the in-app updater's checksum lookup.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root "release"
$ver = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
if (-not $ver) { throw "Directory.Build.props has no <Version>." }
if ($ver -is [array]) { $ver = [string]$ver[0] }

if ($env:GITHUB_REF -like 'refs/tags/v*') {
    $tag = $env:GITHUB_REF_NAME
    $expected = "v$ver"
    if ($tag -ne $expected) {
        throw "Tag '$tag' does not match Directory.Build.props Version $ver (expected $expected). Bump Version (and CHANGELOG, installer default) in the same commit you tag."
    }
}

Write-Host "Packing BitBroom $ver" -ForegroundColor Cyan
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force -Path $release | Out-Null

function Zip-Dist([string]$Runtime, [string]$ZipName) {
    $dist = Join-Path $root "dist\$Runtime"
    if (-not (Test-Path $dist)) { throw "Missing $dist — run ./build/publish.ps1 -Runtime $Runtime first." }
    $zip = Join-Path $release $ZipName
    [System.IO.Compression.ZipFile]::CreateFromDirectory($dist, $zip)
    Write-Host "  $ZipName"
}

Zip-Dist "win-x64" "BitBroom-$ver-portable-win-x64.zip"
Zip-Dist "win-arm64" "BitBroom-$ver-portable-win-arm64.zip"

$iss = Join-Path $root "installer\bitbroom.iss"
& iscc "/DAppVersion=$ver" $iss
if ($LASTEXITCODE -ne 0) { throw "iscc exited $LASTEXITCODE." }
$setupSrc = Join-Path $root "installer\Output\BitBroom-$ver-setup.exe"
if (-not (Test-Path $setupSrc)) { throw "Installer not found at $setupSrc." }
Copy-Item $setupSrc (Join-Path $release "BitBroom-$ver-setup.exe")
Write-Host "  BitBroom-$ver-setup.exe"

$sums = Join-Path $release "SHA256SUMS.txt"
Get-ChildItem $release -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | Sort-Object Name | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "$h  $($_.Name)"
} | Set-Content $sums -Encoding ascii
Write-Host "`nSHA256SUMS.txt:" -ForegroundColor DarkGray
Get-Content $sums
Write-Host "`nDone → $release" -ForegroundColor Green
