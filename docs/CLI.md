# bitbroom-cli reference

The CLI shares the exact engine, guard and catalog with the GUI. Scanning is always
read-only; cleaning requires explicit consent.

```
bitbroom-cli <command> [options]
```

## Commands

### `list`
All categories: id, group, risk, default-set membership, admin requirement.

### `scan`
Measures reclaimable space. Read-only, safe to run anywhere, any time.

```
bitbroom-cli scan                       # the safe default set
bitbroom-cli scan --all                 # + Moderate categories (Advanced never included)
bitbroom-cli scan --categories user-temp,nvidia-caches
bitbroom-cli scan --json                # machine-readable
```

### `clean`
Deletes what a fresh scan finds for the selected categories.

```
bitbroom-cli clean --dry-run            # simulation: full audit log, zero deletions
bitbroom-cli clean --yes                # real clean of the default set
bitbroom-cli clean --categories windows-old --yes    # Advanced ids must be named explicitly
bitbroom-cli clean --min-age 168 --yes  # only files older than 7 days
```

Rules:
- a real clean **requires `--yes`**; without it the command refuses and suggests `--dry-run`;
- `--all` and the default set never include **Advanced** categories — those must be named
  individually via `--categories`;
- files in use are skipped and counted; every deletion lands in the audit log
  (`%LocalAppData%\BitBroom\logs`).

### `hogs`
Report-only inspection of hidden space consumers (hibernation file, page files, WSL/Docker
vhdx disks, System Restore usage*, Windows Search index, WinSxS component store, DriverStore,
Installer cache, event-log store, Outlook OSTs, Downloads folder, CapabilityAccessManager
bug). Each finding includes the supported remediation.

\* restore-point usage needs an elevated terminal.

### `analyze <path>`
Folder-size breakdown, largest files, and a by-file-type summary.

```
bitbroom-cli analyze C:\ --depth 2 --top 25
bitbroom-cli analyze "D:\Projects" --json
```

### `dupes <path>`
Content-verified duplicate report (size → head hash → full SHA-256). **Read-only** —
deleting duplicates is a GUI operation (Recycle Bin-only, one copy per group enforced).
Honors your Settings exclusions.

```
bitbroom-cli dupes D:\Photos --min-size 5          # ≥ 5 MB files only
bitbroom-cli dupes C:\Users\me --json --top 100
```

### `devjunk <path>`
Regenerable developer build folders — `node_modules`, `target`, `.venv`, `dist`, `.next`
and friends — sized and sorted. A folder is only reported when it sits next to the project
manifest that proves its context (`package.json`, `Cargo.toml`, `pyvenv.cfg`, …), so a
folder that merely *shares the name* never appears. **Read-only** — deleting is a GUI
operation (Recycle Bin-only, artifact re-verified at delete time). Honors exclusions.

```
bitbroom-cli devjunk D:\Projects
bitbroom-cli devjunk C:\Users\me --json --top 100
```

### `version`

## Options

| Option | Meaning |
|---|---|
| `--defaults` | Safe default category set (implicit) |
| `--all` | All Safe + Moderate categories |
| `--categories` / `-c` `a,b,c` | Explicit ids from `list` |
| `--dry-run` | Simulation mode (also honoured from GUI settings) |
| `--yes` / `-y` | Consent to delete |
| `--min-age <hours>` | Override minimum file age (0 disables) |
| `--json` | JSON output for scripting |
| `--top <n>` / `--depth <n>` | analyze/dupes/devjunk output shaping |
| `--min-size <mb>` | dupes: smallest file size to consider (default 1 MB) |

Invalid or unknown options (including a mistyped `--dry-run`) are rejected with exit code 3
rather than ignored, so a typo can never turn a simulated run into a real deletion.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | fatal error |
| 2 | completed with skips/errors (files in use, access denied) |
| 3 | bad arguments / refused without `--yes` |
| 4 | needs administrator |
| 5 | aborted (Ctrl-C) |

## Scheduled cleaning example

Weekly clean of the default set, Sundays 09:00 (run from an elevated prompt to include
system categories):

```
schtasks /Create /TN "BitBroom weekly clean" /SC WEEKLY /D SUN /ST 09:00 ^
  /TR "\"C:\Program Files\BitBroom\bitbroom-cli.exe\" clean --yes" /RL HIGHEST
```

Parse results in PowerShell:

```powershell
$r = & bitbroom-cli clean --dry-run --json | ConvertFrom-Json
"{0:N1} GB would be freed" -f ($r.bytesFreed / 1GB)
```
