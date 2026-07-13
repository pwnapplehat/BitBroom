# Category reference

Every cleaning category, its exact targets, risk level and rationale. Sources for each
claim are collected in [RESEARCH.md](RESEARCH.md).

**Risk levels**

- **Safe** — pure caches/logs; applications regenerate them transparently. Only Safe
  categories may be enabled by default (test-enforced).
- **Moderate** — regenerable, but with a real cost (re-downloads, full project restores,
  possible re-sign-ins). Off by default.
- **Advanced** — irreversible consequences. Off by default, explicit warning, excluded
  from CLI `--all`.

**Global protections for rule-based categories:** minimum file age (default 24 h, using the
newer of created/modified — applies to both walked and fixed-file rules), junction/symlink
refusal, cloud-placeholder refusal, locked-file skipping, delete-time revalidation, audit
logging. See [SAFETY.md](SAFETY.md).

**Special categories** (Recycle Bin, Delivery Optimization, Windows.old, Event Logs) do not
run through the file walker; they delegate to the platform's own mechanism (shell API,
`Delete-DeliveryOptimizationCache`, `takeown`/`icacls`/`rd`, `wevtutil`). They are still
audit-logged and confirmation-gated, and their fixed system paths are re-checked before use.

---

## System

| Id | Risk | Default | Admin | Targets |
|---|---|---|---|---|
| `user-temp` | Safe | ✅ | – | `%LocalAppData%\Temp` |
| `windows-temp` | Safe | ✅ | ✅ | `C:\Windows\Temp` |
| `windows-update-cache` | Safe | ✅ | ✅ | `C:\Windows\SoftwareDistribution\Download` |
| `delivery-optimization` | Safe | ✅ | ✅ | DO cache via `Delete-DeliveryOptimizationCache` |
| `crash-dumps` | Safe | ✅ | ✅ | `%LocalAppData%\CrashDumps`, `C:\Windows\Minidump\*.dmp`, `C:\Windows\LiveKernelReports\*.dmp`, `C:\Windows\memory.dmp` |
| `windows-error-reporting` | Safe | ✅ | ✅ | WER `ReportQueue`/`ReportArchive`/`Temp` under ProgramData; `ReportQueue`/`ReportArchive`/`Temp` per-user |
| `windows-logs` | Safe | ✅ | ✅ | `C:\Windows\Logs` (CBS/DISM/MoSetup/waasmedic…), `Panther`, `Debug`, `%ProgramData%\USOShared\Logs`, and the fixed logs `WindowsUpdate.log`/`setupact.log`/`setuperr.log`/`PFRO.log` — all with a 7-day age floor |
| `defender-logs` | Safe | ✅ | ✅ | `%ProgramData%\Microsoft\Windows Defender\Support\*.log` (MPLog…), 7-day floor; detection history & quarantine untouched |
| `thumbnail-cache` | Safe | ✅ | – | Explorer `thumbcache_*.db` / `iconcache_*.db` (locked ones skipped; Tools → Restart Explorer releases) |
| `directx-shader-cache` | Safe | ✅ | – | `%LocalAppData%\D3DSCache` |
| `inet-cache` | Safe | ✅ | – | WinINET `INetCache\IE` + `Content.Outlook` (Office attachment temp) |
| `store-apps-temp` | Safe | ✅ | – | `Packages\*\TempState`, `Packages\*\AC\Temp` (purgeable by platform contract) |
| `recycle-bin` | Moderate | – | – | Shell empty of all bins — off by default because bins hold second thoughts |

## Browsers — caches only, never cookies/history/passwords/site data

| Id | Targets per profile |
|---|---|
| `chrome-cache`, `edge-cache`, `brave-cache`, `vivaldi-cache`, `chromium-cache` | `Cache\Cache_Data`, `Code Cache\js|wasm`, `GPUCache`, `ShaderCache`, `GrShaderCache`, Dawn/Graphite WebGPU caches, `Service Worker\CacheStorage|ScriptCache`, `Media Cache` + shared `ShaderCache`/`GrShaderCache`/`Crashpad\reports` |
| `opera-cache`, `opera-gx-cache` | same set (flat profile layout) + Local `Cache` |
| `firefox-cache` | `cache2`, `startupCache`, `shader-cache`, `jumpListCache` per profile |

All Safe / on-by-default. Running browsers keep some files locked — those are skipped and
picked up next time.

## Applications

| Id | Risk | Default | Notes |
|---|---|---|---|
| `discord-cache` | Safe | ✅ | `Cache`, `Code Cache`, `GPUCache`, `blob_storage`, SW cache |
| `slack-cache` | Safe | ✅ | same Electron set |
| `teams-cache` | Safe | ✅ | new Teams `MSTeams_*\LocalCache\Microsoft\MSTeams` (EBWebView caches, Logs, PerfLogs) + classic Teams caches |
| `spotify-cache` | Safe | ✅ | `LocalCache\Spotify\Data|Browser` (Store) and `%LocalAppData%\Spotify\Data|Browser` — **offline songs in `LocalState\Spotify\Storage` excluded** |
| `adobe-media-cache` | Safe | ✅ | `Media Cache Files`, `Media Cache`, `Peak Files` under `%AppData%\Adobe\Common` |
| `whatsapp-cache` | Safe | ✅ | packaged WhatsApp `LocalCache` |
| `office-cache` | Safe | ✅ | Office `Wef` + `WebServiceCache`; **Document Cache excluded** (unsynced edits) |
| `zoom-logs` | Safe | ✅ | `%AppData%\Zoom\logs` (7-day floor) |
| `apple-device-updates` | Safe | ✅ | iTunes `iPhone/iPad/iPod Software Updates` IPSW installers (5–10 GB each; Apple: safe, re-downloaded on demand). **MobileSync device backups never touched** |
| `onedrive-logs` | Safe | ✅ | `%LocalAppData%\Microsoft\OneDrive\logs` (7-day floor) — synced files and sync state untouched |
| `obs-logs` | Safe | ✅ | `obs-studio\logs`, `crashes` (7-day floor) — scenes/profiles/recordings untouched |
| `docker-desktop-logs` | Safe | ✅ | `%LocalAppData%\Docker\log` (7-day floor) — images/containers live in the WSL vhdx (see Hogs) |
| `java-deployment-cache` | Safe | ✅ | `%LocalAppData%Low\Sun\Java\Deployment\cache` (legacy Web Start) |

## Gaming & GPU

| Id | Risk | Default | Notes |
|---|---|---|---|
| `nvidia-caches` | Safe | ✅ | `DXCache`, `GLCache`, `OptixCache`, `ComputeCache`, `NV_Cache`, ProgramData `Downloader`, `C:\NVIDIA` installer extractions |
| `amd-caches` | Safe | ✅ | `DxCache`, `DxcCache`, `GLCache`, `VkCache`, `C:\AMD` |
| `intel-shader-cache` | Safe | ✅ | `Intel\ShaderCache` |
| `steam-caches` | Safe | ✅ | registry-located install → `steamapps\shadercache|downloading|temp` + `htmlcache`; **games/saves/workshop untouched** |
| `epic-cache` | Safe | ✅ | launcher `webcache` + `webcache_4430`, logs (7-day floor) |
| `ea-cache` | Safe | ✅ | EA Desktop `cache`, logs |
| `battlenet-cache` | Safe | ✅ | `Battle.net\Cache|BrowserCache`, logs (7-day floor) — games/accounts untouched |
| `ubisoft-cache` | Safe | ✅ | `Ubisoft Game Launcher\cache`, logs (7-day floor) — installs/saves/cloud untouched |
| `unity-caches` | Safe | ✅ | `%LocalAppData%\Unity\cache` (global GI/shader caches) |
| `unreal-ddc` | Moderate | – | `UnrealEngine\Common\DerivedDataCache` — regenerable but recooking shaders takes long on big projects |

## Development

| Id | Risk | Default | Notes |
|---|---|---|---|
| `npm-yarn-cache` | Safe | ✅ | `npm-cache`, `Yarn\Cache` — project `node_modules` untouched |
| `pip-uv-cache` | Safe | ✅ | `pip\cache`, `uv\cache` |
| `cargo-cache` | Safe | ✅ | `.cargo\registry\cache|src` (Rust project's own guidance) — installed binaries (`.cargo\bin`) untouched |
| `go-build-cache` | Safe | ✅ | `%LocalAppData%\go-build` (= `go clean -cache`) — module cache (`~\go\pkg\mod`) untouched |
| `nuget-http-cache` | Safe | ✅ | `NuGet\v3-cache|http-cache|plugins-cache` |
| `vs-code-cache` | Safe | ✅ | VS Code / Insiders / **Cursor** / VSCodium: `Cache`, `CachedData`, `Code Cache`, `GPUCache`, `CachedExtensionVSIXs`, SW cache, logs (7-day) |
| `visual-studio-cache` | Safe | ✅ | `ComponentModelCache` (the classic VS fix), `WebsiteCache` |
| `jetbrains-logs` | Safe | ✅ | `JetBrains\*\log` (7-day) |
| `symbol-caches` | Safe | ✅ | `SymbolCache`, `SymCache` (debugger PDB stores) |
| `nuget-global-packages` | Moderate | – | full restore on next build |
| `gradle-cache` | Moderate | – | `.gradle\caches` |
| `maven-repository` | Moderate | – | `.m2\repository` |

## Advanced

| Id | Risk | Default | Admin | Notes |
|---|---|---|---|---|
| `store-apps-localcache` | Moderate | – | – | all packages' `LocalCache` (7-day floor) — some apps re-prompt sign-in |
| `upgrade-leftovers` | Moderate | – | ✅ | `$Windows.~BT`, `$Windows.~WS`, `$GetCurrent`, `$SysReset`, `C:\ESD` — not while an update is pending |
| `windows-old` | **Advanced** | – | ✅ | kills rollback; may contain old profile files — check `Windows.old\Users` first |
| `event-logs` | **Advanced** | – | ✅ | clears diagnostic history via `wevtutil` |
| `custom-folders` | Moderate | – | – | **your** folders from Settings → Custom folders, each with its own age limit. Anchored at the folder's parent and validated by the full PathGuard: protected locations (Documents, Desktop, Windows, Program Files, OneDrive, drive roots…) are refused at scan time even if added |

---

## Deliberately excluded

Documented refusals — each with reasoning in [RESEARCH.md](RESEARCH.md):

| What | Why not |
|---|---|
| Registry cleaning | No measurable benefit, real breakage risk (Russinovich; Microsoft unsupported) |
| `C:\Windows\Prefetch` | Self-managing; purging slows the next boots (documented since XP) |
| `C:\Windows\Installer` | Breaks uninstall/repair/update of installed software |
| `WebCache` ESE database | Live database backing INetCache bookkeeping |
| Browser cookies/history/passwords/IndexedDB | User data & sessions, not junk |
| Office Document Cache (`OfficeFileCache`) | Can contain **unsynced document edits** |
| `$WinREAgent` | Used by pending recovery-environment servicing |
| Downloads folder auto-clean | User content; surfaced in Analyzer/Hogs for manual review instead |
| DISM `/ResetBase` | Permanently removes update-uninstall; marginal gain |
| Boot-time deletion of locked files | Racing the owning process for a few MB |
| pagefile/hiberfil deletion as "cleaning" | System-managed; exposed as explicit, reversible Tools instead |
| winapp2.ini import | Thousands of unvetted third-party delete rules (incl. registry ops) would bypass the tested guard model; every BitBroom rule is individually researched and gate-tested instead |
| Startup manager / uninstaller / driver updater | Windows Settings & Task Manager do this natively; suite-creep is how cleaners rot |
| Secure wipe / free-space shredding | Privacy tool, not a space tool; ineffective on SSDs and punishing on any drive |

## Adding a category (contributors)

1. Ground it: vendor doc, Microsoft doc, or strong community evidence. Link it in RESEARCH.md.
2. Express it declaratively in `CategoryCatalog` — if you need custom code, it's a special
   category with its own review bar.
3. Only `Safe` may be `EnabledByDefault` (test-enforced).
4. `dotnet test` must stay green — including `CatalogSanityTests`, which resolves your rule
   against a real machine and fails on any guard rejection.
5. State what the category must **never** touch (like Spotify's `Storage`), and encode that
   as exclusion, not hope.
