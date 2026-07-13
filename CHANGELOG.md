# Changelog

All notable changes to BitBroom are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); versioning follows SemVer.

## [1.2.1] — 2026-07-13

### Changed
- **Tools page redesigned around discoverability**: instead of a strip of bare buttons,
  every tool is now a card with a plain-language description of what it does, what you
  gain, and any caveat — plus an **Admin badge** on tools that need elevation. Tools are
  grouped (Storage & disks / Hibernation & power / Quick fixes) with the live output
  console docked alongside, so you can read what a tool is while another one runs.

### Fixed
- **WSL fstrim step actually runs now.** The distro name was passed quoted, which
  `wsl.exe` rejects (`WSL_E_DISTRO_NOT_FOUND`), so the trim silently fell back to
  "skipped" and compaction reclaimed less than it should. Names are now passed unquoted
  (with a safety skip for exotic names containing whitespace).
- `wsl.exe` output is now decoded as UTF-16, so its messages appear correctly in the
  Tools console instead of as spaced-out garbage.

### Verified
- Full **Compact WSL / Docker disks** flow exercised on a real machine (Ubuntu +
  Docker Desktop): trims ran in both distros, all three `.vhdx` disks compacted
  (~1.2 GB reclaimed), Ubuntu booted normally afterwards and Docker Desktop relaunched.
- All README screenshots retaken, including the redesigned Tools page.

## [1.2.0] — 2026-07-13

Driven by a second deep research pass across Reddit, Microsoft Q&A and the Docker/WSL
issue trackers: the loudest unsolved complaints were WSL/Docker `.vhdx` files that never
shrink, the DriverStore hoarding years of superseded drivers, OneDrive's buried
"Free up space", and gigabytes of forgotten `node_modules`. All four are now actions,
not just reports.

### Added
- **Tools → Compact WSL / Docker disks** — the fix for the single biggest hidden hog we
  previously only *reported*. Runs the documented recipe end-to-end: best-effort `fstrim`
  inside every distro, `wsl --shutdown`, then `diskpart` `attach vdisk readonly` +
  `compact vdisk` per disk (the read-only attach enables the zero-block scan, same as
  `Optimize-VHD -Mode Full` — but works on Home edition, no Hyper-V needed). Only removes
  blocks the guest filesystem already freed; a failure always triggers a detach pass so a
  disk is never left attached.
- **Tools → Free up OneDrive space** — bulk "make online-only" for every detected OneDrive
  folder (the same `attrib +U -P` pin/unpin mechanism behind Explorer's own right-click →
  Free up space, which Windows 11 made hard to find). Deletes nothing: cloud copies stay,
  files re-download on open.
- **Tools → Remove old drivers** — clears the DriverStore of *superseded* driver versions
  via `pnputil`. Keeps the newest version of every driver family, never offers unique
  drivers or same-version duplicates (they can serve different hardware IDs), and never
  passes `/force`/`/uninstall` — Windows itself refuses any package still in use.
  Verified end-to-end on a real machine (superseded Intel Bluetooth package removed,
  newest kept, re-enumeration confirmed).
- **Duplicates → Dev junk mode** — finds regenerable developer build folders
  (`node_modules`, `target`, `.venv`/`venv` with `pyvenv.cfg`, `dist`, `build`, `out`,
  `.next`, `.nuxt`, `.turbo`, `.parcel-cache`, `.gradle`, `__pycache__`, …) with a
  manifest guard: a folder only qualifies next to a real `package.json`/`Cargo.toml`/
  `pom.xml`/etc., so a folder that merely shares the name is never listed. Recycle
  Bin-only, artifact re-verified immediately before recycling (a folder whose project
  vanished since the scan is refused), matched folders never recursed into.
- **CLI: `devjunk <path>`** — the same finder as a read-only report (`--top`, `--json`).

### Deliberately not added (researched and rejected)
- Auto-cleaning `C:\Windows\Installer` orphans — Microsoft explicitly states there is no
  supported way to prune the Installer cache; it stays report-only in Space Hogs.
- "Uninstall leftover" deletion — matching leftover AppData folders to uninstalled
  programs is heuristic and can hit data belonging to software you still use.

## [1.1.3] — 2026-07-13

### Changed
- **Scheduled cleaning is now registered from a Task Scheduler XML definition** with
  `StartWhenAvailable` (a run missed because the PC was off/asleep catches up next time
  it's on) and run-on-battery enabled, instead of a bare `schtasks /Create`. BitBroom still
  needs no background process or startup entry — the reassurance is now also in the
  Settings copy. Verified end-to-end that the task fires and cleans with the app closed.

## [1.1.2] — 2026-07-13

### Fixed
- The **Duplicates** navigation item and Scan button had no icon: the chosen glyph
  (`CopySelect24`) is a valid enum value but has no glyph in the bundled Fluent font, so it
  rendered blank. Switched to `DocumentMultiple24`, which renders.

### Changed
- `ManualDeleteGuard` now detects drive-root system items **dynamically** by their
  Hidden+System attributes (catching pagefile, hiberfil, `$Recycle.Bin`, System Volume
  Information, Recovery, etc. without relying on the name list), keeping the curated names
  only as a fallback for unreadable/missing items. Protected trees were already resolved
  dynamically from the environment.

## [1.1.1] — 2026-07-13

### Fixed
- **Safety: the Disk Analyzer's per-row recycle now goes through a guard.** It previously
  called the shell delete directly and showed a delete icon next to protected locations
  (Windows, Program Files, ProgramData, the Users root, drive-root system files). A new
  shared `ManualDeleteGuard` refuses those for both the Analyzer and Duplicates tabs, and
  the delete icon is hidden for any non-deletable row. Content inside your own profile
  stays deletable.

### Changed
- Roomier default window (1400×910, up from 1280×820), clamped to the monitor work area so
  it still fits smaller laptop screens.

## [1.1.0] — 2026-07-13

The "world-class" feature release, driven by a competitive research pass across
CCleaner / BleachBit / Wise / WizTree / PC Manager and community pain points.

### Added
- **Duplicates tab**: content-verified duplicate file finder — three-stage pipeline
  (size → 128 KB head hash → full SHA-256) so large unique files are never fully read
  and a match is never a guess. Grouped results with keep-newest/keep-oldest auto-select.
  Deletion is **Recycle Bin-only** and **one copy of every group always survives**,
  enforced in the engine (`DuplicateDeleter`), not just the UI. Windows/Program Files
  are excluded from scans by design.
- **Empty folders mode** (same tab): finds truly empty folders (nested empties count;
  a folder holding a junction never does). Re-verified file-free immediately before
  each Recycle Bin deletion.
- **Scheduled cleaning** (Settings): per-user Windows Task Scheduler task running
  `bitbroom-cli clean --yes` daily/weekly/monthly at your chosen hour — free,
  no elevation, reconciled with the real task state on load.
- **Custom folders category**: add your own folders (Settings) with a per-folder age
  limit; they appear as a Moderate, off-by-default category. The PathGuard still
  refuses protected locations — adding Documents does nothing.
- **Exclusions** (Settings): folders BitBroom must never touch, enforced during root
  expansion, enumeration and again at delete time; also honored by the duplicate and
  empty-folder finders and the CLI.
- **Recycle Bin clean mode** (Settings, off by default): cleans send files to the bin
  instead of deleting permanently — an undo window while you build trust, with the
  honest caveat that space frees only when the bin is emptied.
- **12 new researched categories**: Zoom logs, Apple device updates (IPSW), OneDrive
  logs, OBS logs & crash dumps, Docker Desktop logs, Java deployment cache, Battle.net
  cache, Ubisoft Connect cache, Unity editor caches, Unreal DerivedDataCache (Moderate),
  Rust cargo registry cache, Go build cache — catalog now counts 60.
- **Analyzer: file-type breakdown** (top extensions by size) and **CSV export** of the
  largest files + type stats. CLI `analyze` prints/embeds the same data.
- **CLI `dupes <path>`** command: read-only duplicate report with `--min-size`, `--top`,
  `--json`.

### Changed
- Audit log gained a `BIN` marker for Recycle Bin deletions; scan notes now show
  "N excluded by you" when exclusions skip entries.
- Space Hogs: CapabilityAccessManager guidance cites the official fix (KB5095093).

## [1.0.0] — 2026-07-12

### UI revamp — native Windows 11 Fluent
- Adopted the MIT-licensed [WPF UI](https://github.com/lepoco/wpfui) library (the GUI's
  single third-party dependency; engine and CLI remain dependency-free): FluentWindow
  with **acrylic wallpaper-blur backdrop** (taskbar-style, with a 65% smoke tint for
  contrast; falls back to the stock solid dark background on Windows 10 or when
  transparency effects are disabled in Settings), native title bar, real NavigationView with
  Fluent System Icons, Fluent buttons/checkboxes/toggle switches/combo boxes/scrollbars,
  Card surfaces and InfoBars.
- Views are cached across tab switches (scroll positions and in-flight scans survive).
- Kept: the branded splash intro, page transitions, staggered reveals, shimmer progress,
  smooth scrolling, brand accent (#38BDF8) and the Display-cut typography.
- Splash intro is centered and plays a synthesized broom-sweep chime (whoosh + three
  "bit" sparkles matching the animation); toggleable in Settings → Appearance & sound.
- In-app updates: optional once-per-launch GitHub release check (Settings → Updates,
  on by default, disable for zero network requests) with a banner offering one-click
  install — the installer is SHA-256-verified against the release's SHA256SUMS.txt
  before it runs. The installer shows a visible progress window and surfaces any error,
  installs per-user (no UAC), and restarts the app when done.
- Removed the now-redundant hand-rolled chrome (caption buttons, nav rail, control
  templates, DWM interop).

### Hardening (full source audit)
- Fixed a crash on launching a second instance (single-instance mutex was released by a
  process that never owned it).
- Fixed the Analyzer per-item "Open in Explorer"/"Recycle" buttons and the Space Hogs
  "Open location" button, which were bound to a view model that didn't expose those
  commands (the buttons did nothing when clicked).
- Fixed-file cleaning rules (e.g. `memory.dmp`, the fixed Windows logs) now honour the
  minimum-age filter like every other rule; the fixed logs carry the 7-day floor.
- CLI argument parsing is now strict: unknown options, missing values and malformed numbers
  are rejected with exit code 3 instead of being silently ignored — a mistyped `--dry-run`
  can no longer fall through to a real clean.
- Hardened the Tools console against a `StringBuilder` cross-thread race; made the Analyzer
  "Recycle" run off the UI thread; the Clean tab's Simulation badge and the Dashboard drive
  bars now refresh live; cancelled scans no longer leave a stale "Clean" button enabled.
- Recycle Bin scan no longer allocates one record per bin entry (bounded memory on huge
  bins); removed dead code/settings/converters; corrected a few icon glyphs; fixed the
  installer AppId to a valid GUID and pinned the CI Inno Setup version.

Initial release.

### Added
- **Cleaning engine** with layered safety model: PathGuard (double validation),
  reparse-point refusal everywhere, cloud-placeholder protection, min-age filter using the
  newer of mtime/ctime, locked-file skipping, per-run audit logs, simulation mode.
- **48 cleaning categories** across System, Browsers, Applications, Gaming & GPU,
  Development and Advanced groups — every path research-grounded (see docs/RESEARCH.md):
  Windows temp/Update/Delivery Optimization caches, crash dumps & LiveKernelReports, WER,
  Windows/Defender logs, thumbnail & DirectX shader caches, INetCache, Store-app TempState,
  Chromium-family + Firefox caches, Discord/Slack/Teams/Spotify/WhatsApp/Adobe/Office
  caches, NVIDIA/AMD/Intel/Steam/Epic/EA caches, npm/Yarn/pip/uv/NuGet/VS/VS Code/JetBrains/
  Gradle/Maven/symbol caches, Recycle Bin, upgrade leftovers, Windows.old, event logs.
- **Disk Analyzer**: parallel folder-size tree + top-100 largest files; recycle-bin
  deletions only.
- **Space Hogs** report: hiberfil/pagefile, WSL & Docker vhdx disks, System Restore usage,
  Windows Search index, WinSxS, DriverStore, Installer cache, event-log store, Outlook
  OSTs, Downloads folder, and detection of the 2026 CapabilityAccessManager.db-wal bug —
  each with supported remediation guidance.
- **Tools**: DISM component-store analyze/cleanup (never /ResetBase), hibernation
  off/reduced/on, DNS flush, Explorer restart, shadow-storage report; live console output.
- **WPF GUI**: dark fluent-style theme (zero UI dependencies), Dashboard with drive usage
  and lifetime stats, grouped category list with risk badges and per-category scan notes,
  non-admin graceful degradation with in-app elevation, confirmation overlay, status bar.
- **Motion design** (all hand-rolled on WPF's composited animation system — still zero
  dependencies): branded startup intro (broom sweeps "bits" away, wordmark pops, splash
  cross-dissolves into a gently zooming shell), page transitions on every tab switch,
  staggered card/list reveals, animated drive-usage bars, hover/press micro-interactions
  on buttons, nav items (sliding accent indicator), checkboxes (back-ease check pop) and
  list rows, shimmer progress bars during scans, smooth inertial mouse-wheel scrolling,
  animated combo popups and scrollbar thumbs, and Windows 11 native rounded corners via
  DWM.
- **CLI (`bitbroom-cli`)**: list/scan/clean/hogs/analyze with `--dry-run`, `--yes`,
  `--json`, `--min-age`, scripting exit codes, sandbox base overrides for integration
  testing.
- **Safety test suite** (71 tests): junction canary, guard matrix, deleter semantics,
  age filters, wildcard expansion, whole-catalog resolution gate.
- Publishing: self-contained single-file exes (x64/ARM64), Inno Setup installer script,
  GitHub Actions CI with release automation.

### Excluded on purpose
Registry cleaning, prefetch purging, `C:\Windows\Installer`, browser cookies/history,
Office Document Cache, `$WinREAgent`, DISM `/ResetBase`, boot-time forced deletions —
rationale in docs/CATEGORIES.md.
