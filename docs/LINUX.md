# Developing BitBroom from Linux

BitBroom is a Windows app (`net10.0-windows` + WPF). It **cannot compile or run on Ubuntu**.
GitHub Actions `windows-latest` is the build machine. That is enough for small changes:
the unit suite (PathGuard, RuntimeAppGuard, junction canary, catalog sanity) runs on
every push. Click-through on a real Windows desktop is reserved for major UI/behavior work.

## Everyday loop

```bash
# in BitBroom/
git checkout main
# edit, commit
git push origin main          # CI: restore, build, test, publish x64+arm64, pack release assets
```

Watch [Actions](https://github.com/pwnapplehat/BitBroom/actions). A green `CI` job is the
gate. Download the `BitBroom-release` artifact if you want the zips/installer without tagging.

## Shipping a release (no local Windows)

1. Bump `<Version>` in `Directory.Build.props`, add a `CHANGELOG.md` entry, commit, push, wait for green CI.
2. Tag the **same** version (CI refuses a mismatch — that protects the in-app updater):

```bash
git tag -a v1.2.6 -m "BitBroom v1.2.6"
git push origin v1.2.6
```

3. The tag job publishes a GitHub Release with:
   - `BitBroom-<ver>-portable-win-x64.zip`
   - `BitBroom-<ver>-portable-win-arm64.zip`
   - `BitBroom-<ver>-setup.exe` (what the in-app updater downloads)
   - `SHA256SUMS.txt` (GNU text mode, two spaces — required by `UpdateService`)

## What Ubuntu cannot do

- `dotnet build` / `dotnet test` / `dotnet run` of this repo (WPF targeting pack is Windows-only).
- Launching the GUI, compacting a real WSL/Docker vhdx, taking README screenshots.

Those need a Windows desktop, and only when the change is major enough to be worth it.
