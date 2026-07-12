# Changelog

All notable changes to BitBroom are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); versioning follows SemVer.

## [1.0.0] — 2026-07-12

### UI revamp — native Windows 11 Fluent
- Adopted the MIT-licensed [WPF UI](https://github.com/lepoco/wpfui) library (the GUI's
  single third-party dependency; engine and CLI remain dependency-free): FluentWindow
  with **acrylic wallpaper-blur backdrop** (taskbar-style, with a smoke tint for contrast;
  falls back to solid dark on Windows 10), native title bar, real NavigationView with
  Fluent System Icons, Fluent buttons/checkboxes/toggle switches/combo boxes/scrollbars,
  Card surfaces and InfoBars.
- Views are cached across tab switches (scroll positions and in-flight scans survive).
- Kept: the branded splash intro, page transitions, staggered reveals, shimmer progress,
  smooth scrolling, brand accent (#38BDF8) and the Display-cut typography.
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
