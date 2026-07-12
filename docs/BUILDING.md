# Building BitBroom

## Prerequisites

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (LTS)

## Build & test

```powershell
dotnet build BitBroom.sln
dotnet test  tests\BitBroom.Core.Tests\BitBroom.Core.Tests.csproj
```

The test suite is the safety gate — it includes the junction-canary test and a
whole-catalog resolution check against your machine. It must be green before any PR.

## Run from source

```powershell
dotnet run --project src\BitBroom.App          # GUI
dotnet run --project src\BitBroom.Cli -- scan  # CLI
```

Developer GUI switches (available in all builds; used for docs screenshots and smoke tests):
`--tab <0-6>` opens a specific page, `--autoscan` triggers that page's read-only scan on
load. `build\capture.ps1` automates window screenshots for the README.

## Publish release binaries

```powershell
.\build\publish.ps1                     # win-x64 → dist\win-x64
.\build\publish.ps1 -Runtime win-arm64  # ARM64
```

Produces self-contained single-file `BitBroom.exe` (GUI) and `bitbroom-cli.exe` — no
runtime install needed on target machines. WPF cannot be IL-trimmed, so the GUI is ~65 MB
(compressed single file).

## Installer

With [Inno Setup 6](https://jrsoftware.org/isinfo.php) installed:

```powershell
iscc installer\bitbroom.iss     # → installer\Output\BitBroom-<version>-setup.exe
```

## CI

`.github/workflows/ci.yml` builds, runs the full test suite, and publishes self-contained
x64 + ARM64 binaries as downloadable build artifacts on every push, PR, and `v*` tag.

## Cutting a release

Releases are produced from locally built and smoke-tested artifacts so every published
binary is verified on a real machine first:

```powershell
# 1. Build both architectures
./build/publish.ps1 -Runtime win-x64
./build/publish.ps1 -Runtime win-arm64

# 2. Zip the portable builds and build the installer
Compress-Archive dist/win-x64/*   release/BitBroom-<ver>-portable-win-x64.zip
Compress-Archive dist/win-arm64/* release/BitBroom-<ver>-portable-win-arm64.zip
iscc installer/bitbroom.iss        # -> installer/Output/BitBroom-<ver>-setup.exe

# 3. Tag and publish (attach the zips, the installer, and SHA256SUMS.txt)
git tag -a v<ver> -m "BitBroom v<ver>"
git push origin v<ver>
gh release create v<ver> release/* --title "BitBroom v<ver>" --notes-file <notes>
```

## Engine integration testing (sandboxed)

The CLI honours test-only environment variables that re-anchor rule bases, so you can run
a **real** clean against a sandbox without touching the actual system:

```powershell
$env:BITBROOM_TEST_LOCALAPPDATA = "C:\sandbox\LocalAppData"
bitbroom-cli clean --categories user-temp --yes
```

All guard rules still apply inside the sandbox. (`BITBROOM_TEST_ROAMINGAPPDATA` and
`BITBROOM_TEST_PROGRAMDATA` exist too.)
