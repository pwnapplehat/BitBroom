# Installs a pinned Inno Setup so CI (and a Windows machine) produce the same compiler.
#
#   ./build/install-innosetup.ps1
#
# BitBroom's wizard is stock "modern" (no 6.7-only theming), but the compiler is still
# pinned: an unpinned chocolatey/winget install would let two builds of the same commit
# differ. SHA-256 + Authenticode are checked before it runs.
param(
    [string]$Version = "6.7.3",
    [string]$Sha256 = "9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732",
    [string]$ExpectedSigner = "Pyrsys B.V."
)
$ErrorActionPreference = "Stop"

$tag = "is-" + ($Version -replace '\.', '_')
$url = "https://github.com/jrsoftware/issrc/releases/download/$tag/innosetup-$Version.exe"
$installer = Join-Path $env:TEMP "innosetup-$Version.exe"
$target = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6"

Write-Host "Inno Setup $Version <- $url" -ForegroundColor Cyan
Invoke-WebRequest $url -OutFile $installer -UseBasicParsing -TimeoutSec 300

$actual = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $Sha256.ToLower()) {
    throw "Inno Setup SHA-256 mismatch.`n  expected $Sha256`n  actual   $actual`nRefusing to install an unexpected build. If the pin is genuinely out of date, verify the new release by hand and update -Sha256."
}
Write-Host "  sha256 OK" -ForegroundColor DarkGray

$sig = Get-AuthenticodeSignature $installer
if ($sig.Status -ne "Valid") {
    throw "Inno Setup installer signature is $($sig.Status), expected Valid."
}
if ($sig.SignerCertificate.Subject -notlike "*$ExpectedSigner*") {
    throw "Inno Setup installer signed by an unexpected publisher: $($sig.SignerCertificate.Subject)"
}
Write-Host "  signature OK ($($sig.SignerCertificate.Subject))" -ForegroundColor DarkGray

$p = Start-Process $installer -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ALLUSERS" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Inno Setup installer exited with $($p.ExitCode)." }

$iscc = Join-Path $target "ISCC.exe"
if (-not (Test-Path $iscc)) {
    $iscc = Get-ChildItem "${env:ProgramFiles(x86)}", $env:ProgramFiles -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $iscc) { throw "ISCC.exe not found after installing Inno Setup $Version." }

$isccDir = Split-Path -Parent $iscc
if ($env:GITHUB_PATH) { Add-Content -Path $env:GITHUB_PATH -Value $isccDir }
$env:PATH = "$isccDir;$env:PATH"

$reported = (& $iscc | Select-String -Pattern 'Inno Setup \d' | Select-Object -First 1).Line
Write-Host "Inno Setup ready: $iscc" -ForegroundColor Green
if ($reported) { Write-Host "  $($reported.Trim())" -ForegroundColor DarkGray }
