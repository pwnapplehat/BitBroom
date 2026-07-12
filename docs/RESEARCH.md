# Research: why BitBroom cleans what it cleans

This document records the research behind BitBroom's category catalog and safety decisions.
It was compiled in July 2026 from Microsoft documentation, vendor guidance, community
databases and years of user pain documented on Reddit, Microsoft Q&A, Super User and
specialist forums. Where a claim below is not directly sourced, it is marked as community
consensus.

---

## 1. The problem users actually have

The recurring shape of the complaint (r/techsupport, r/WindowsHelp, Microsoft Q&A) is not
"my temp folder is big" — it's **"my C: drive filled up and I cannot see why."** The culprits
are almost always in one of three buckets:

1. **Regenerable caches nobody ever clears** — GPU shader caches, Electron app caches,
   media caches, package-manager caches. Individually boring; together, tens of GB.
2. **Hidden system consumers Explorer won't show** — `hiberfil.sys`, shadow copies inside
   `System Volume Information`, the search index, WSL/Docker `.vhdx` disks that grow but
   never shrink, dump files in `C:\Windows\LiveKernelReports`.
3. **Bugs and pathological growth** — the headline example in 2026 being
   `CapabilityAccessManager.db-wal`.

### The 2026 CapabilityAccessManager bug (why "Space Hogs" exists)

A Windows 11 24H2/25H2 bug causes the Capability Access Manager service (`camsvc`) to grow
its write-ahead log without compaction. Confirmed cases reached **200–513 GB**. Microsoft
fixed it in KB5095093 (June 2026 optional update; July 2026 Patch Tuesday for everyone).
Manual deletion requires Safe Mode and must touch only the `.db-wal` file — users who
deleted more reported broken Wi-Fi.

- Windows Latest (2026-07-06): "Microsoft admits a Windows 11 bug is eating up to 500GB"
  — https://www.windowslatest.com/2026/07/06/microsoft-admits-a-windows-11-bug-is-eating-up-to-500gb-of-storage-verify-if-you-are-affected/
- gHacks (2026-07-08): https://www.ghacks.net/2026/07/08/microsoft-confirms-windows-11-bug-that-can-consume-over-500gb-of-storage-through-permission-log-file/
- Remediation details: https://www.wintips.org/how-to-fix-capabilityaccessmanager-db-wal-taking-up-huge-disk-space-on-windows-11/

BitBroom **detects and explains** this (Space Hogs) rather than deleting a live system
database out from under a running service.

### Why classic cleaners miss the real hogs

Storage Sense handles user temp files, the Recycle Bin and OneDrive dehydration, but is
deliberately conservative about system areas; the classic Disk Cleanup (`cleanmgr`) is
deprecated-but-present and still needed for update leftovers. Neither surfaces WSL disks,
shadow-copy usage, the search index, or vendor caches.
(https://learn.microsoft.com/en-us/windows/configuration/storage/storage-sense)

---

## 2. Evidence per category group

### Windows Update caches

`C:\Windows\SoftwareDistribution\Download` is safe to empty once updates are installed;
Windows re-downloads what it needs. Deleting **while an update is mid-install can corrupt
the OS** — hence BitBroom's skip-locked behaviour and min-age filter.
- https://learn.microsoft.com/en-us/answers/questions/5522428/
- https://superuser.com/questions/53266/

Delivery Optimization keeps its peer-cache under
`C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache`;
Microsoft ships a supported cmdlet, `Delete-DeliveryOptimizationCache`, which BitBroom uses.
- https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-faq
- https://learn.microsoft.com/en-us/windows/deployment/do/delivery-optimization-test

### Crash dumps & kernel live dumps

`C:\Windows\LiveKernelReports` accumulates multi-GB `WATCHDOG`/`USBHUB3`/`NDIS` live dumps
on healthy systems; Microsoft documents the folder, community consensus is they're safe to
delete unless you're actively debugging.
- https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-code-reference-live-dump
- https://www.elevenforum.com/t/can-i-delete-this-massive-file-watchdog-dmp.40294/
- https://deep.data.blog/2015/06/08/the-strange-case-of-the-large-livekernelreports-folder/

### Defender logs

`C:\ProgramData\Microsoft\Windows Defender\Support\MPLog-*.log` grows unbounded on some
systems (a Spiceworks thread documents servers with 900k files in the Scans history);
Microsoft moderators confirm the Support logs are safe to delete — the live one stays
locked and BitBroom skips it.
- https://learn.microsoft.com/en-us/answers/questions/4308743/windows-defender-support-logs
- https://community.spiceworks.com/t/windows-defender-filling-disk-with-thousands-of-files/798489

### GPU shader caches

NVIDIA's own support article instructs deleting `%LocalAppData%\NVIDIA\DXCache`, `GLCache`
and `NV_Cache` to fix corruption; caches rebuild on next launch. Same model for AMD
(`DxCache`, `DxcCache`, `GLCache`, `VkCache`), Intel (`ShaderCache`) and Windows' own
`%LocalAppData%\D3DSCache`. Reddit threads regularly show 10 GB+ DXCache folders.
- https://nvidia.custhelp.com/app/answers/detail/a_id/5735/
- Winapp2.ini `[NVIDIA Shader Cache *]` section (community-verified paths)

### Browser caches

Chromium profile caches (`Cache\Cache_Data`, `Code Cache`, `GPUCache`, `ShaderCache`,
`Service Worker\CacheStorage`, Dawn/Graphite WebGPU caches) and Firefox's `cache2` are
regenerable by definition. BitBroom **never** touches cookies, history, passwords,
sessions, IndexedDB or Local Storage — privacy cleaning is a different job with different
risks (mass logouts), and mixing the two is how cleaners destroy user trust.

### Electron / communication apps

Discord's own cache trio (`Cache`, `Code Cache`, `GPUCache`) routinely exceeds 5–10 GB.
New Teams' documented cache location is
`%LocalAppData%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams`; Microsoft's
guidance confirms clearing it re-syncs from the cloud.
- https://learn.microsoft.com/en-us/troubleshoot/microsoftteams/teams-administration/clear-teams-cache
- https://cleanor.app/blog/how-to-clear-the-discord-cache-on-pc

### Spotify

The Store app's streaming cache lives in `…\SpotifyAB.SpotifyMusic_*\LocalCache\Spotify\Data`
and grows ~10 GB every couple of weeks for heavy users; **downloads live separately in
`LocalState\Spotify\Storage`**, which BitBroom deliberately excludes so offline music
survives.
- https://community.spotify.com/t5/Desktop-Windows/localState-folder-and-localCache-folder/td-p/5153299
- https://community.spotify.com/t5/Desktop-Windows/Spotify-Cache-keeps-clogging-up-my-PC/td-p/7417138

### Adobe media cache

Puget Systems (workstation integrator) documents Premiere/AE media caches reaching
"hundreds of GB"; Adobe staff confirm `%AppData%\Adobe\Common\Media Cache*` is safe to
delete with apps closed and regenerates on demand.
- https://www.pugetsystems.com/labs/articles/how-to-configure-storage-and-cache-file-locations-in-premiere-pro-2292/
- https://creativecow.net/forums/thread/adobe-appdata-folder/

### Steam & launchers

Valve community + support consensus: `steamapps\shadercache`, `steamapps\downloading`,
`steamapps\temp` and `htmlcache` are safe; `steamapps\common` (games) must never be
touched. BitBroom locates Steam via `HKCU\Software\Valve\Steam\SteamPath` instead of
guessing paths.
- https://steamcommunity.com/discussions/forum/1/6679490060452861540/

### Developer caches

Official docs for every ecosystem: `npm cache clean --force` exists precisely because the
cache is disposable; `pip cache purge`; `dotnet nuget locals all --clear`. The NuGet
**global-packages** folder and Gradle/Maven stores are bigger wins but force full
re-restores — BitBroom ships them **off by default** at Moderate risk.
- https://docs.npmjs.com/cli/v11/commands/npm-cache/
- https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders

### WSL / Docker virtual disks

The single most common "invisible 100 GB" on developer machines. Dynamic `.vhdx` files grow
with usage and **never shrink automatically**; the fix is prune → `fstrim` → `wsl --shutdown`
→ `Optimize-VHD`/`diskpart compact vdisk` (and the missing-`fstrim` step is why most guides
fail). BitBroom finds every vhdx (distro packages, `%LocalAppData%\wsl`, Docker's
`docker_data.vhdx`) and prints the exact procedure.
- https://stackoverflow.com/questions/70946140/docker-desktop-wsl-ext4-vhdx-too-large
- https://github.com/docker/desktop-feedback/issues/366

### WinSxS (component store)

Microsoft: never delete from WinSxS manually — it can make Windows unbootable. The
supported reductions are the StartComponentCleanup scheduled task and
`DISM /Online /Cleanup-Image /StartComponentCleanup`. The apparent folder size **overstates
reality because of hardlinks** shared with System32.
`/ResetBase` permanently removes the ability to uninstall current updates; BitBroom
deliberately does not automate it.
- https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-8.1-and-8/dn251565(v=win.10)

### Windows.old & upgrade leftovers

Windows deletes `Windows.old` itself ~10 days after upgrade; removing it earlier is
supported via cleanup tooling and kills rollback. It can contain old user-profile files —
BitBroom's warning tells users to check `Windows.old\Users` first, and the category is
Advanced/off-by-default.
- https://support.microsoft.com/en-us/help/4028075/windows-delete-your-previous-version-of-windows

### Hibernation & Fast Startup

`hiberfil.sys` defaults to ~40% of RAM. `powercfg /h off` removes it (also disabling Fast
Startup); `powercfg /h /type reduced` keeps Fast Startup at ~20% of RAM. BitBroom exposes
both as explicit Tools with plain-language trade-offs.
- https://www.howtogeek.com/885071/how-to-disable-hibernation-and-remove-hiberfil-sys-in-windows-11/

### System Restore / shadow copies

Shadow storage hides inside `System Volume Information` and can consume 10 %+ of a volume;
`vssadmin list shadowstorage` reports it, resizing (`vssadmin resize shadowstorage … /maxsize=5%`)
caps it while keeping the newest points. Deleting all points removes the rollback net —
BitBroom reports and guides, never bulk-deletes.
- https://learn.microsoft.com/en-us/answers/questions/5658932/

### Windows Search index

`Windows.edb`/`Windows.db` under `%ProgramData%\Microsoft\Search` reaches tens of GB when
PST/OST content is indexed (KB2952967 documents the mechanism). Fix is rebuild or offline
`esentutl /d` compaction — both surfaced as guidance.
- https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/troubleshoot-windows-search-performance-issues
- https://woshub.com/windows-edb-file-too-big-how-to-reduce-size/

### Store apps (packaged apps)

Per the UWP application-data contract, `TempState` is purgeable at any time (apps must
tolerate it) — safe, on by default. `LocalCache` is *usually* safe but some apps stash
session state there, so it ships as a separate Moderate/off category. `LocalState` is app
data and is never touched.
- https://www.systutorials.com/where-are-the-data-of-metro-apps-stored-in-windows-10/
- https://learn.microsoft.com/en-us/archive/msdn-technet-forums/a9f30534-8d91-4bdf-8864-3a64f4b71e04

### DriverStore

GPU vendors leave 1 GB+ per superseded driver package in
`System32\DriverStore\FileRepository`; the safe removal path is `pnputil /delete-driver`
or the open-source Driver Store Explorer (RAPR) — manual deletion is unsupported. BitBroom
reports size + points at the supported tools.
- https://superuser.com/questions/1126332/
- https://github.com/MicrosoftDocs/windows-driver-docs (pnputil syntax)

---

## 3. Myths we refuse to implement

**Prefetch cleaning.** `C:\Windows\Prefetch` is self-limiting and self-maintaining; purging
it makes the next boots and app launches *slower* while Windows relearns. Documented since
the XP era by Microsoft's own performance engineers ("not only is deleting the directory
totally unnecessary, but you're also putting a temporary dent in your PC's performance" —
Ryan Myers, Windows Client Performance Team) and by Ed Bott's testing.
- https://edbott.com/2005/06/01/one-more-time-do-not-clean-out-your-prefetch-folder/

**Registry cleaning.** Mark Russinovich (Sysinternals/Azure CTO): registry bloat has
essentially no performance impact and a safe+effective cleaner would require "a huge amount
of application-specific knowledge" — he refused to ever build one. Microsoft officially
does not support registry cleaners and warns problems they cause may be unrepairable.
- https://superuser.com/questions/349161/what-do-registry-cleaners-actually-do
- https://learn.microsoft.com/en-us/answers/questions/2463239/

**Forcing deletion of locked files / boot-time deletion.** The reward is a few MB; the risk
is racing the owner process. BitBroom counts and reports locked files instead.

**Wholesale `AppData\Local\Packages` deletion.** Resets Store apps, loses sign-ins and
local databases. Only `TempState` (and optionally `LocalCache`) are contract-purgeable.

---

## 4. Why cleaners themselves are a risk surface

CCleaner ≤ 6.33 had **CVE-2025-3025**: its cleaning feature followed symbolic links /
junctions during deletion, exploitable for SYSTEM-level privilege escalation
(CWE-552 "Link Following"). The entire class dies if the engine simply refuses to traverse
reparse points — which is BitBroom's hard rule, enforced at scan time, wildcard-expansion
time and delete time, and covered by a canary test.
- https://nvd.nist.gov/vuln/detail/CVE-2025-3025

The product context matters too: the community's migration from CCleaner (bloat, popups,
telemetry concerns, buried manual controls in v7 — see the CCleaner community forum threads
from 2025-2026) toward BleachBit/Cleanmgr+ shows what users actually want: **manual
control, transparency, no upsells**. That's the product brief BitBroom is built to.

---

## 5. Cross-validation

Category paths were cross-checked against two long-lived community databases:

- **Winapp2.ini** (~3,400 cleaning sections, maintained since 2010) — used as a *reference*
  for path verification only; BitBroom's rules are hand-written and guard-validated.
  https://github.com/MoscaDotTo/Winapp2
- **BleachBit CleanerML** definitions. https://github.com/bleachbit/bleachbit

Every rule shipped must additionally pass `CatalogSanityTests`, which resolves the whole
catalog on the build machine and fails if any rule even *attempts* a protected location.

---

## 6. Platform notes

- Built on **.NET 10 (LTS, released 2025-11-11, supported to 2028-11-14)** with WPF.
  https://dotnet.microsoft.com/en-us/platform/support/policy
- Long paths are handled by .NET automatically; the manifest additionally declares
  `longPathAware`.
- x64 and ARM64 publishing supported; 32-bit is not (simplifies P/Invoke marshaling).
