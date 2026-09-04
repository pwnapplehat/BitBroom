# Building BitBroom

## Prerequisites

- **To compile locally:** Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (LTS). WPF cannot build on Linux.
- **From Ubuntu:** you do not need those. Push to GitHub; Actions `windows-latest` compiles, tests, and on a `v*` tag publishes the GitHub Release. See [LINUX.md](LINUX.md).

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

`.github/workflows/ci.yml` runs on `windows-latest` for every push, PR, and `v*` tag:
restore, build, the full test suite, publish x64 + ARM64, pack portable zips + the Inno
setup.exe + `SHA256SUMS.txt`, and upload those as Actions artifacts.

On a version tag whose name matches `<Version>` in `Directory.Build.props` (`v1.2.6`
when Version is `1.2.6`; a mismatch fails the job), it also creates the GitHub Release
the in-app updater consumes. No local Windows machine is required for that path.

## Cutting a release

Preferred path is from Linux or anywhere with `git`: bump Version + CHANGELOG, push,
wait for green CI, tag. Details: [LINUX.md](LINUX.md).

If you *are* on Windows and want to pack locally (optional):

```powershell
./build/publish.ps1 -Runtime win-x64
./build/publish.ps1 -Runtime win-arm64
./build/install-innosetup.ps1    # once, pinned + checksum-verified
./build/pack-release.ps1         # → release\ zips + setup.exe + SHA256SUMS.txt
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
