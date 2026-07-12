# The BitBroom safety model

Disk cleaning is inherently destructive, so BitBroom is engineered like the failure mode
is unforgivable — because it is. This document describes the concrete mechanisms, in the
order they execute.

## Layer 0 — rules are declarative and structurally constrained

A cleaning rule is data, not code:

```
Base            one of: LocalAppData, RoamingAppData, LocalLow, UserProfile,
                ProgramData, SystemRoot, SystemDrive, Custom(trusted provider)
RelativePattern e.g.  Google\Chrome\User Data\Profile *\Cache
FilePatterns    e.g.  *.log  thumbcache_*.db   (default: *)
MinAge          per-rule override of the global minimum file age
```

Structural validation (enforced at construction, covered by tests):

- pattern must be **relative** — rooted patterns are rejected;
- pattern may not contain `..`;
- `Custom` bases must come from a trusted provider (e.g. Steam's install path read from
  `HKCU\Software\Valve\Steam`), never from user-editable text.

Wildcard segments (`Profile *`, `MSTeams_*`) are expanded **by enumerating real
directories** — never by string substitution — and any directory that is a reparse point
is dropped from expansion, so a junction cannot smuggle a foreign tree into a rule.

## Layer 1 — PathGuard validates every resolved root

Before a rule's resolved folder is ever walked:

1. must be fully qualified and deeper than its base by at least one segment
   (defence against an environment variable resolving too broadly);
2. must not **be** a drive root, the base itself, `%WinDir%`, `%ProgramData%`,
   `%UserProfile%`, `%LocalAppData%`, `%AppData%`, the `Users` root, or the
   `Packages` root;
3. must not be **inside**: Desktop, Documents, Pictures, Music, Videos, Downloads,
   any OneDrive root, Program Files (x86 too), `System32`, `SysWOW64`, `WinSxS`,
   `Fonts`, `Boot`, `servicing`, `SystemApps`, `SystemResources`;
4. rules based on the **system drive** may only target an explicit allow-list of
   well-known leftovers: `NVIDIA`, `AMD`, `Intel`, `Windows.old`, `$Windows.~BT`,
   `$Windows.~WS`, `$GetCurrent`, `$SysReset`, `ESD`;
5. the root itself must not be a reparse point.

*(Exception: rules with a trusted Custom base — the Steam install directory — may live
under Program Files, because that is simply where Steam is. Everything else still applies.)*

## Layer 2 — the walker never follows reparse points

During scanning, every directory entry's attributes are checked:

- directory with `ReparsePoint` → **not traversed**, counted, reported in the UI;
- file with `ReparsePoint`, `Offline`, `RECALL_ON_DATA_ACCESS` or `RECALL_ON_OPEN`
  (OneDrive & other cloud placeholders) → **never yielded**;
- files younger than the minimum age — using the **newer of mtime and ctime**, so
  freshly-extracted archives with old mtimes are still protected — are kept.

This kills the vulnerability class behind CCleaner's CVE-2025-3025 (junction-following
deletion) by construction.

## Layer 3 — SafeDeleter re-validates at the moment of deletion

Scan results can go stale, so deletion re-checks everything (TOCTOU defence):

1. path must still be inside the root it was scanned under (`PathGuard.ValidateDeletePath`);
2. attributes are re-read; reparse points and cloud placeholders are refused *again*;
3. locked files (sharing violations) are **skipped and counted** — never forced, never
   scheduled for reboot deletion;
4. read-only files get one attribute-clear retry; access-denied is counted, not escalated;
5. empty directories are removed bottom-up with non-recursive deletes only
   (a non-empty directory physically cannot be deleted by mistake), and the rule root
   itself is always preserved.

Simulation mode short-circuits step 3+ into log-only writes.

## Layer 4 — special operations use the platform's own mechanism

| Operation | Mechanism |
|---|---|
| Recycle Bin | `SHEmptyRecycleBin` shell API |
| Delivery Optimization | `Delete-DeliveryOptimizationCache` PowerShell cmdlet |
| Event logs | `wevtutil el` / `wevtutil cl` per channel |
| WinSxS | `DISM /Online /Cleanup-Image /StartComponentCleanup` (never `/ResetBase`) |
| Hibernation | `powercfg /hibernate …` |
| Windows.old | `takeown`/`icacls` then removal — with an Advanced-risk warning, off by default |
| Analyzer deletions | `SHFileOperation` with `FOF_ALLOWUNDO` → **always Recycle Bin** |

## Layer 5 — the human layer

- Scanning is **read-only**, always.
- Cleaning shows a confirmation with per-risk warnings (configurable off).
- Risk levels: **Safe** (pure caches; the only level allowed to be on-by-default — enforced
  by a test), **Moderate** (regenerable with a cost), **Advanced** (irreversible
  consequences; explicit warnings; excluded from CLI `--all`).
- Every run writes an audit log: `DEL/WOULD <size> <full path>` per file, `SKIP <reason>`
  for everything refused.
- Lifetime counters and last-clean time keep the tool honest about what it did.

## The canary test

`WalkerAndDeleterTests.Junction_targets_are_never_touched` builds this trap in a sandbox:

```
cache\
  junk1.tmp            ← must be deleted
  sub\junk2.tmp        ← must be deleted
  link  →  ..\precious  (NTFS junction)
precious\
  canary.txt           ← must SURVIVE
```

The suite fails if the canary is touched, if the junction is traversed, or if the junction
itself is deleted. Additional tests cover: guard rejection of every protected location,
delete-time root escapes, cloud-placeholder attributes, locked/read-only/missing files,
age filtering by both timestamps, wildcard expansion skipping junctions, and a
whole-catalog resolution gate (`CatalogSanityTests`) that fails CI if any shipped rule
even attempts a protected path on the build machine.

## What BitBroom will never do

No registry cleaning. No prefetch purging. No `C:\Windows\Installer` or `WebCache`
deletion. No browser cookies/history/passwords. No Office Document Cache (unsynced edits).
No `$WinREAgent` (pending servicing). No forced deletion of locked files. No network calls.

If a future contribution proposes any of these, the answer is in this file.
